using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.HallOfFame;

/// <summary>
/// Снимок Зала славы (PLAN.md, §3.4: «топ-3, топ-клан»), задача #137 (E8). Задача Hangfire <c>hall-of-fame</c> — каждый
/// час по Минску: сезон попадает в Зал один раз, когда он закрыт (<see cref="ScoreBook.ClosesAt"/> — 04:00 первого дня
/// следующего) и смена на следующий сезон выполнена (<c>seasons.reset_at</c>); в остальные часы задача видит отметку.
/// </summary>
/// <remarks>
/// <para>
/// <b>Тот же отбор, что итог рейтинга территории.</b> Очки — <see cref="ScoreBook.FinalTotalsAsync"/>: начисления, видимые
/// на момент закрытия. Не по <c>reset_at</c> и не по «сейчас»: в момент смены очки последних минут сезона ещё приходят, и
/// снимок навсегда разошёлся бы с итогом сезона (docs/architecture/scoring-and-seasons.md, «Итог сезона»). Места —
/// как в рейтинге: одинаковые очки — одинаковое место (1, 2, 2), в Зал идут места 1–3.
/// </para>
/// <para>
/// <b>Клан</b> — сумма итоговых очков его участников в лиге (решено 15.10 по делегированию; так и в образце ответа). Участники —
/// те, кто в клане в момент снимка и вступил до конца сезона: вступивший уже в новом сезоне не приносит клану очки прошлого.
/// Кто вышел до снимка, истории членства нет — его очки клану не идут. Неполный клан (меньше 3 человек) не исключается: §3.6
/// отстраняет его только от рейдов и контроля кварталов.
/// </para>
/// <para>
/// <b>Один раз.</b> Отметка в <c>job_runs</c> ставится в той же транзакции, под блокировкой (10): повтор задачи (Hangfire,
/// второй экземпляр сервера во время деплоя) ничего не меняет. Ники не хранятся — их читает <c>GET /hall-of-fame</c> по
/// согласию игрока; удаление аккаунта обезличивает строку (база: <c>user_id</c> → <c>null</c>).
/// </para>
/// </remarks>
public sealed class HallOfFameSnapshots(AppDbContext db, SeasonStore seasons, TimeProvider time, ILogger<HallOfFameSnapshots> logger)
{
    /// <summary>Отметка снимка в <c>job_runs</c>; ключ — номер сезона.</summary>
    public const string Job = "hall-of-fame";

    /// <summary>Пространство блокировки снимка Зала славы (docs/architecture/jobs.md, «Пространства блокировок»).</summary>
    public const int LockSpace = 10;

    /// <summary>Мест игроков в Зале (§3.4: «топ-3»).</summary>
    public const int PlayerPlaces = 3;

    /// <summary>Задача по расписанию: снимок закрытых сезонов, которых ещё нет в Зале. Возвращает число новых строк.</summary>
    public async Task<int> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var calendar = await seasons.CalendarAsync(cancellationToken);
        var reset = await db.Seasons.AsNoTracking().Where(s => s.ResetAt != null).Select(s => s.Number).ToListAsync(cancellationToken);
        var done = await db.JobRuns.AsNoTracking().Where(j => j.Job == Job).Select(j => j.Key).ToListAsync(cancellationToken);

        // Закрыт и сменён: смена на пропущенный сезон не догоняется (SeasonRollover), поэтому годится и смена на любой более
        // поздний сезон.
        var due = calendar.Seasons
            .Where(s => ScoreBook.ClosesAt(calendar, s.Number) is { } closesAt && closesAt <= now
                && reset.Any(r => r > s.Number)
                && !done.Contains(s.Number))
            .ToList();
        var written = 0;
        foreach (var season in due)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, 0)", cancellationToken);
            if (await db.JobRuns.AnyAsync(j => j.Job == Job && j.Key == season.Number, cancellationToken))
            {
                continue; // второй экземпляр сервера успел раньше
            }

            var end = calendar.EndOf(season)!.Value; // у закрытого сезона конец есть
            var members = await db.ClanMembers.AsNoTracking()
                .Where(m => m.JoinedAt < end)
                .Join(db.Clans, m => m.ClanId, c => c.Id, (m, c) => new { m.UserId, c.Id, c.Name })
                .ToListAsync(cancellationToken);
            var rows = new List<HallOfFameEntity>();
            foreach (var league in Enum.GetValues<League>())
            {
                var totals = await ScoreBook.FinalTotalsAsync(db, calendar, league, season.Number, now, cancellationToken)
                    ?? new Dictionary<Guid, int>();
                rows.AddRange(Top(totals, PlayerPlaces).Select(t => new HallOfFameEntity
                {
                    Season = season.Number,
                    League = league,
                    Kind = HallOfFameKind.Player,
                    Place = t.Place,
                    UserId = t.Key,
                    Value = t.Value,
                    CreatedAt = now,
                }));

                var clans = members
                    .GroupBy(m => (m.Id, m.Name))
                    .ToDictionary(g => g.Key, g => g.Sum(m => totals.GetValueOrDefault(m.UserId)));
                rows.AddRange(Top(clans, places: 1).Select(t => new HallOfFameEntity
                {
                    Season = season.Number,
                    League = league,
                    Kind = HallOfFameKind.Clan,
                    Place = t.Place,
                    ClanId = t.Key.Id,
                    ClanName = t.Key.Name,
                    Value = t.Value,
                    CreatedAt = now,
                }));
            }

            db.HallOfFame.AddRange(rows);
            db.JobRuns.Add(new JobRunEntity { Job = Job, Key = season.Number, DoneAt = now });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Зал славы сезона {Season}: {Rows} строк", season.Number, rows.Count);
            written += rows.Count;
        }

        return written;
    }

    /// <summary>
    /// Места 1…<paramref name="places"/> по очкам: одинаковые очки — одинаковое место (1, 2, 2, 4), как в рейтингах; без
    /// очков в Зал не попадают.
    /// </summary>
    private static IEnumerable<(TKey Key, int Value, int Place)> Top<TKey>(Dictionary<TKey, int> values, int places)
        where TKey : notnull
    {
        var ordered = values.Values.Where(v => v > 0).OrderDescending().ToList();
        return values
            .Where(v => v.Value > 0)
            .Select(v => (v.Key, v.Value, Place: 1 + ordered.Count(o => o > v.Value)))
            .Where(v => v.Place <= places)
            .OrderBy(v => v.Place);
    }
}
