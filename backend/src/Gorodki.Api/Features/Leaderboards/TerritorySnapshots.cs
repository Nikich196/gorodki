using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Scoring;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Leaderboards;

/// <summary>
/// Суточный срез очков и рейтинг территории (PLAN.md, §3.5: «удержание: ежедневный срез в 00:00 по Минску, ступени, потолок
/// в зачёт»; «рейтинги — по ежедневному снимку, раздельно по лигам»), задача #136 (E7). Задача Hangfire
/// <c>territory-snapshot</c> — каждый час по Минску: в 00:00 делает срез, в остальные часы видит, что он уже есть; в момент
/// закрытия сезона (<see cref="ScoreBook.ClosesAt"/>, 04:00 первого дня следующего) — итоговый проход по прошлому сезону.
/// </summary>
/// <remarks>
/// <para>
/// <b>Только видимое (§3.16).</b> Удержание — по площади тронутой в сезоне земли такой, какой её видят другие на карте
/// (<see cref="TerritoryReader.VisibleOwnedAreaAsync(Guid, League, TerritoryViewer, DateTimeOffset?, CancellationToken)"/>,
/// посторонний зритель): захват последних 20–25 минут ещё не прибавлен, скрытый чужой захват земли игрока ещё не вычтен.
/// Очки — только видимые начисления (<see cref="ScoreBook.SeasonTotalsAsync"/>); итог — <see cref="ScoreBook.FinalTotalsAsync"/>.
/// </para>
/// <para>
/// <b>Один раз.</b> Срез за сутки и итог сезона отмечаются в <c>job_runs</c> в той же транзакции, под блокировкой (6); строка
/// удержания уникальна на (игрок, лига, сутки) — повтор задачи второй раз не начисляет и не переснимает.
/// </para>
/// </remarks>
public sealed class TerritorySnapshots(
    AppDbContext db,
    GameConfigStore configs,
    SeasonStore seasons,
    TerritoryReader territory,
    TimeProvider time,
    ILogger<TerritorySnapshots> logger)
{
    /// <summary>Отметка суточного среза в <c>job_runs</c>; ключ — номер игровых суток среза.</summary>
    public const string DailyJob = "territory-daily";

    /// <summary>Отметка итогового прохода в <c>job_runs</c>; ключ — номер сезона.</summary>
    public const string FinalJob = "territory-final";

    /// <summary>Пространство блокировки среза территории (docs/architecture/jobs.md, «Пространства блокировок»).</summary>
    public const int LockSpace = 6;

    /// <summary>Сколько дней хранить ежедневные срезы (итог сезона хранится дольше).</summary>
    public const int KeepDays = LeaderboardSnapshots.KeepDays;

    /// <summary>Слой среза территории — лига: «Бег» в слое <c>foot</c>, «Вело» — в <c>bike</c>.</summary>
    public static LeaderboardLayer LayerOf(League league) => league == League.Bike ? LeaderboardLayer.Bike : LeaderboardLayer.Foot;

    /// <summary>Задача по расписанию: суточный срез, если его ещё нет, и итог закрытых сезонов. Возвращает число строк.</summary>
    public async Task<int> RunIfDueAsync(CancellationToken cancellationToken) =>
        await TakeDailyIfDueAsync(cancellationToken) + await TakeFinalIfDueAsync(cancellationToken);

    /// <summary>
    /// Срез за текущие игровые сутки: удержание за прошедшие сутки (если они были в сезоне) и места по очкам сезона — идущего
    /// и того, к которому относились прошедшие сутки (в первый день сезона это прошлый сезон, его рейтинг пока
    /// предварительный). Возвращает число новых строк (удержание и места); 0 — срез уже был.
    /// </summary>
    public async Task<int> TakeDailyIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var day = GameClock.GameDayOf(now);
        if (await DoneAsync(DailyJob, day.DayNumber, cancellationToken))
        {
            return 0;
        }

        var calendar = await seasons.CalendarAsync(cancellationToken);
        var rules = (await configs.GetCurrentAsync(cancellationToken)).Rules;
        var holdDay = day.AddDays(-1);
        var holdAt = SeasonCalendar.MinskMidnight(holdDay);
        var holdSeason = calendar.At(holdAt);

        // Удержание — до транзакции записи: площадь читается своим снимком базы (TerritoryReader открывает транзакцию сам).
        // Вне сезона удержания нет: «касались в этом сезоне» (§3.4) без сезона не определено (решено 07.10 по делегированию).
        var holds = new List<ScoreEventEntity>();
        if (holdSeason is not null)
        {
            var delay = TimeSpan.FromMinutes(rules.Privacy.PublicEventDelayMinutes);
            foreach (var league in Enum.GetValues<League>())
            {
                foreach (var userId in await HoldCandidatesAsync(league, holdSeason.StartsAt, TerritoryReader.PublicHorizon(now, delay), cancellationToken))
                {
                    var area = await territory.VisibleOwnedAreaAsync(
                        userId, league, new TerritoryViewer(null, Immediate: false), holdSeason.StartsAt, cancellationToken);
                    var score = Scores.ForHold(area, rules.Scoring);
                    if (score.Points <= 0)
                    {
                        continue;
                    }

                    holds.Add(new ScoreEventEntity
                    {
                        UserId = userId,
                        League = league,
                        Season = holdSeason.Number,
                        GameDay = holdDay,
                        Kind = ScoreKind.Hold,
                        Points = score.Points,
                        Basis = Math.Round(score.Basis, 3),
                        EffectiveAt = holdAt,
                        VisibleAt = now, // видно с момента среза: площадь уже по публичной проекции
                        CreatedAt = now,
                    });
                }
            }
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, 0)", cancellationToken);
        if (await DoneAsync(DailyJob, day.DayNumber, cancellationToken))
        {
            return 0; // второй экземпляр сервера успел раньше
        }

        var paid = (await db.ScoreEvents
                .Where(e => e.Kind == ScoreKind.Hold && e.GameDay == holdDay)
                .Select(e => new { e.UserId, e.League })
                .ToListAsync(cancellationToken))
            .Select(e => (e.UserId, e.League))
            .ToHashSet();
        var fresh = holds.Where(h => !paid.Contains((h.UserId, h.League))).ToList();
        db.ScoreEvents.AddRange(fresh);
        await db.SaveChangesAsync(cancellationToken);

        // Места — по очкам сезона после записи удержания. Сезон, у которого уже снят итог, не переснимается.
        var finals = await db.JobRuns.AsNoTracking().Where(j => j.Job == FinalJob).Select(j => j.Key).ToListAsync(cancellationToken);
        var boards = new[] { calendar.At(now)?.Number, holdSeason?.Number }.OfType<int>().Distinct().Where(s => !finals.Contains(s));
        var rows = new List<LeaderboardSnapshotEntity>();
        foreach (var season in boards)
        {
            foreach (var league in Enum.GetValues<League>())
            {
                rows.AddRange(Ranked(day, league, season, await ScoreBook.SeasonTotalsAsync(db, calendar, league, season, now, cancellationToken), final: false));
            }
        }

        db.LeaderboardSnapshots.AddRange(rows);
        db.JobRuns.Add(new JobRunEntity { Job = DailyJob, Key = day.DayNumber, DoneAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await db.LeaderboardSnapshots
            .Where(s => s.Board == LeaderboardBoard.Territory && !s.Final && s.Day < day.AddDays(-KeepDays))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Срез территории за {Day}: удержание {Holds}, мест {Rows}", day, fresh.Count, rows.Count);
        return fresh.Count + rows.Count;
    }

    /// <summary>
    /// Итог закрытых сезонов (момент закрытия — <see cref="ScoreBook.ClosesAt"/>): места по <see cref="ScoreBook.FinalTotalsAsync"/>,
    /// один раз на сезон; предварительные места этого сезона за сегодня заменяются итоговыми. Возвращает число строк итога.
    /// </summary>
    public async Task<int> TakeFinalIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var calendar = await seasons.CalendarAsync(cancellationToken);
        var closed = calendar.Seasons
            .Where(s => ScoreBook.ClosesAt(calendar, s.Number) is { } closesAt && closesAt <= now)
            .Select(s => s.Number)
            .ToList();
        var done = await db.JobRuns.AsNoTracking().Where(j => j.Job == FinalJob).Select(j => j.Key).ToListAsync(cancellationToken);
        var written = 0;
        foreach (var season in closed.Except(done))
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, 0)", cancellationToken);
            if (await DoneAsync(FinalJob, season, cancellationToken))
            {
                continue;
            }

            var day = GameClock.GameDayOf(now);
            await db.LeaderboardSnapshots
                .Where(s => s.Board == LeaderboardBoard.Territory && s.Season == season && s.Day == day && !s.Final)
                .ExecuteDeleteAsync(cancellationToken);
            var rows = new List<LeaderboardSnapshotEntity>();
            foreach (var league in Enum.GetValues<League>())
            {
                var totals = await ScoreBook.FinalTotalsAsync(db, calendar, league, season, now, cancellationToken) ?? new Dictionary<Guid, int>();
                rows.AddRange(Ranked(day, league, season, totals, final: true));
            }

            db.LeaderboardSnapshots.AddRange(rows);
            db.JobRuns.Add(new JobRunEntity { Job = FinalJob, Key = season, DoneAt = now });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Итог рейтинга территории сезона {Season}: {Rows} мест", season, rows.Count);
            written += rows.Count;
        }

        return written;
    }

    /// <summary>
    /// Кому считать удержание: владельцы земли лиги, которой касались с начала сезона, и те, чью такую землю взял ещё скрытый
    /// захват (на публичной карте она пока у них). Аккаунты, которые удаляются, — нет.
    /// </summary>
    private async Task<List<Guid>> HoldCandidatesAsync(
        League league, DateTimeOffset seasonStart, DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        var owners = db.Parcels.AsNoTracking()
            .Where(p => p.League == league && p.TouchedAt >= seasonStart)
            .Select(p => p.OwnerId);
        var takenRecently = db.CaptureJournalPieces.AsNoTracking()
            .Where(p => !p.After && p.TouchedAt >= seasonStart)
            .Join(
                TerritoryReader.HiddenJournal(db, league, horizon),
                p => new { p.CaptureId, p.TileX, p.TileY },
                j => new { j.CaptureId, j.TileX, j.TileY },
                (p, j) => p.OwnerId);
        return await owners.Concat(takenRecently)
            .Distinct()
            .Where(id => db.Users.Any(u => u.Id == id && u.DeletionRequestedAt == null))
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    private Task<bool> DoneAsync(string job, int key, CancellationToken cancellationToken) =>
        db.JobRuns.AnyAsync(j => j.Job == job && j.Key == key, cancellationToken);

    /// <summary>Места по очкам: одинаковые очки — одинаковое место (1, 2, 2, 4), как у «Кто открыл больше».</summary>
    private static IEnumerable<LeaderboardSnapshotEntity> Ranked(
        DateOnly day, League league, int season, Dictionary<Guid, int> totals, bool final)
    {
        var values = totals.Values.OrderDescending().ToList();
        return totals.Select(t => new LeaderboardSnapshotEntity
        {
            Day = day,
            Board = LeaderboardBoard.Territory,
            Layer = LayerOf(league),
            Season = season,
            UserId = t.Key,
            Value = t.Value,
            Rank = 1 + values.Count(v => v > t.Value),
            Final = final,
        });
    }
}
