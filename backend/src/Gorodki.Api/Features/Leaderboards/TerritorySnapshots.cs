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
/// Удержание и рейтинг территории (PLAN.md, §3.5: «удержание: ежедневный срез в 00:00 по Минску, ступени, потолок в зачёт»;
/// «рейтинги — раздельно по лигам»), задача #136 (E7). Задача Hangfire <c>territory-snapshot</c> — каждые 5 минут по Минску:
/// места по очкам сезона переснимаются каждый проход (docs/plan-deviations.md, №15: «всё открыто и работает сразу»), удержание
/// за прошедшие сутки начисляется один раз — первым проходом после полуночи; в момент закрытия сезона
/// (<see cref="ScoreBook.ClosesAt"/>, 04:00 первого дня следующего) — итоговый проход по прошлому сезону.
/// </summary>
/// <remarks>
/// <para>
/// <b>Только видимое (§3.16).</b> Удержание — по площади тронутой в сезоне земли такой, какой её видят другие на карте
/// (<see cref="TerritoryReader.VisibleOwnedAreaAsync(Guid, League, TerritoryViewer, DateTimeOffset?, CancellationToken)"/>,
/// посторонний зритель): захват последних 20–25 минут ещё не прибавлен, скрытый чужой захват земли игрока ещё не вычтен.
/// Очки — только видимые начисления (<see cref="ScoreBook.SeasonTotalsAsync"/>); итог — <see cref="ScoreBook.FinalTotalsAsync"/>.
/// Поэтому частый пересчёт ничего не выдаёт раньше карты: очки захвата попадают в места с той же границы, что сам захват на
/// карте, — первым проходом после неё, то есть не позже чем через 5 минут.
/// </para>
/// <para>
/// <b>Один раз — удержание и итог.</b> Удержание за сутки и итог сезона отмечаются в <c>job_runs</c> в той же транзакции,
/// под блокировкой (6); строка удержания уникальна на (игрок, лига, сутки) — повторный проход второй раз не начисляет.
/// <b>Места — заменой.</b> Строки текущих суток стираются и пишутся заново под той же блокировкой: второй экземпляр
/// сервера дублей не даст, а таблица не растёт — за прошлые сутки остаётся их последний проход, хранится неделя.
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
    /// <summary>Отметка удержания в <c>job_runs</c>; ключ — номер игровых суток, первым проходом которых оно начислено.</summary>
    public const string DailyJob = "territory-daily";

    /// <summary>Отметка итогового прохода в <c>job_runs</c>; ключ — номер сезона.</summary>
    public const string FinalJob = "territory-final";

    /// <summary>Пространство блокировки среза территории (docs/architecture/jobs.md, «Пространства блокировок»).</summary>
    public const int LockSpace = 6;

    /// <summary>Сколько дней хранить срезы прошлых суток (итог сезона хранится дольше).</summary>
    public const int KeepDays = LeaderboardSnapshots.KeepDays;

    /// <summary>Слой среза территории — лига: «Бег» в слое <c>foot</c>, «Вело» — в <c>bike</c>.</summary>
    public static LeaderboardLayer LayerOf(League league) => league == League.Bike ? LeaderboardLayer.Bike : LeaderboardLayer.Foot;

    /// <summary>
    /// Задача по расписанию (каждые 5 минут): удержание за прошедшие сутки, если его ещё не было, места по очкам сезона и итог
    /// закрытых сезонов. Возвращает число записанных строк (удержание, места, итог).
    /// </summary>
    public async Task<int> RunIfDueAsync(CancellationToken cancellationToken) =>
        await PayHoldIfDueAsync(cancellationToken) + await RankAsync(cancellationToken) + await TakeFinalIfDueAsync(cancellationToken);

    /// <summary>
    /// Удержание за прошедшие игровые сутки (если они были в сезоне) — один раз за сутки, первым проходом после полуночи по
    /// Минску (§3.5). Возвращает число новых строк удержания; 0 — уже начислено (или некому).
    /// </summary>
    public async Task<int> PayHoldIfDueAsync(CancellationToken cancellationToken)
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
                        VisibleAt = now, // видно с момента начисления: площадь уже по публичной проекции
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
        db.JobRuns.Add(new JobRunEntity { Job = DailyJob, Key = day.DayNumber, DoneAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Удержание за {Day}: {Holds} начислений", holdDay, fresh.Count);
        return fresh.Count;
    }

    /// <summary>
    /// Места по очкам сезона за текущие игровые сутки — заменой: строки сегодняшнего дня стираются и пишутся заново, прошлые
    /// сутки остаются своим последним проходом. Сезоны — все начатые без снятого итога: идущий и, до итога в 04:00 первого
    /// дня нового сезона, прошлый (его места пока предварительные). Возвращает число записанных мест.
    /// </summary>
    public async Task<int> RankAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var day = GameClock.GameDayOf(now);
        var calendar = await seasons.CalendarAsync(cancellationToken);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, 0)", cancellationToken);

        // Сезон, у которого уже снят итог, не переснимается: его места — итоговые (TakeFinalIfDueAsync).
        var finals = await db.JobRuns.AsNoTracking().Where(j => j.Job == FinalJob).Select(j => j.Key).ToListAsync(cancellationToken);
        var boards = calendar.Seasons.Where(s => s.StartsAt <= now).Select(s => s.Number).Except(finals).ToList();
        var rows = new List<LeaderboardSnapshotEntity>();
        foreach (var season in boards)
        {
            foreach (var league in Enum.GetValues<League>())
            {
                rows.AddRange(Ranked(day, league, season, await ScoreBook.SeasonTotalsAsync(db, calendar, league, season, now, cancellationToken), final: false));
            }
        }

        await db.LeaderboardSnapshots
            .Where(s => s.Board == LeaderboardBoard.Territory && s.Day == day && !s.Final && boards.Contains(s.Season))
            .ExecuteDeleteAsync(cancellationToken);
        db.LeaderboardSnapshots.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        await db.LeaderboardSnapshots
            .Where(s => s.Board == LeaderboardBoard.Territory && !s.Final && s.Day < day.AddDays(-KeepDays))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Места рейтинга территории за {Day}: {Rows}", day, rows.Count);
        return rows.Count;
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
