using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Scoring;
using Gorodki.Domain.Territory;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Scoring;

/// <summary>
/// Книга очков (PLAN.md, §3.5): начисления за захваты и дистанцию (<see cref="ScoreEventEntity"/>) — запись и чтение.
/// Как считается — docs/architecture/scoring-and-seasons.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Приватность (§3.16).</b> Всё, что читают другие (рейтинги, срез E7, «Мои данные»), — только через
/// <see cref="Visible"/>: начисление с <c>visible_at</c> не позже «сейчас». У захвата это граница публичности его применения
/// (<c>TerritoryReader.VisibleAtAsync</c>, как у карты), у дистанции — момент подсчёта (он и так после границы: визиты и
/// дистанция считаются, когда конец забега уже публичен). Свои очки за захват игрок тоже видит с этой границы: очки
/// зависят от разбивки итога (бонус за вражескую землю и снятый уровень), а разбивка до границы выдала бы ещё скрытый
/// чужой захват — поэтому её не видит и автор (C1, docs/architecture/captures.md).
/// </para>
/// <para>
/// «Очки → 0» при смене сезона (§3.4) — это новый номер сезона у новых начислений; старые остаются историей.
/// </para>
/// </remarks>
public static class ScoreBook
{
    /// <summary>Начисления, которые уже видны всем (<c>visible_at ≤ now</c>). Всё, что показывается другим, — только отсюда.</summary>
    public static IQueryable<ScoreEventEntity> Visible(AppDbContext db, DateTimeOffset now) =>
        db.ScoreEvents.AsNoTracking().Where(e => e.VisibleAt <= now);

    /// <summary>
    /// Очки сезона по игрокам лиги — сумма видимых на <paramref name="now"/> начислений (опора для суточного среза E7).
    /// Игроки без начислений в ответ не входят.
    /// </summary>
    public static async Task<Dictionary<Guid, int>> SeasonTotalsAsync(
        AppDbContext db, League league, int season, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await Visible(db, now)
            .Where(e => e.League == league && e.Season == season)
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Points = g.Sum(e => e.Points) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.UserId, r => r.Points);
    }

    /// <summary>
    /// Начисление за применённый захват — в транзакции захвата, под блокировкой игрока (сумма за сутки считается под ней).
    /// Ноль очков (петля только освежила свою землю, всё под щитом…) не пишется. Добавляет строку в контекст — она
    /// сохранится вместе с землёй.
    /// </summary>
    public static async Task AddCaptureAsync(
        AppDbContext db,
        CaptureEntity claim,
        IReadOnlyDictionary<PieceOutcome, double> areaByOutcome,
        double landValue,
        DateTimeOffset effectiveAt,
        DateTimeOffset visibleAt,
        int? season,
        ScoringConfig config,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var day = GameClock.GameDayOf(effectiveAt);
        var before = await DaySumAsync(db, claim.UserId, claim.League, day, ScoreKind.Capture, cancellationToken);
        var score = Scores.ForCapture(areaByOutcome, before, landValue, config);
        if (score.Points <= 0)
        {
            return;
        }

        db.ScoreEvents.Add(new ScoreEventEntity
        {
            UserId = claim.UserId,
            League = claim.League,
            Season = season,
            GameDay = day,
            Kind = ScoreKind.Capture,
            Points = score.Points,
            Basis = Math.Round(score.Basis, 3),
            CaptureId = claim.Id,
            RunId = claim.RunId,
            EffectiveAt = effectiveAt,
            VisibleAt = visibleAt,
            CreatedAt = now,
        });
    }

    /// <summary>
    /// Начисление за дистанцию забега — в транзакции визитов, под блокировкой игрока (потолок за сутки считается под ней).
    /// Возвращает засчитанные метры (0 — сутки уже выбраны или путь пуст). Ноль очков не пишется.
    /// </summary>
    public static async Task<double> AddDistanceAsync(
        AppDbContext db,
        RunEntity run,
        double meters,
        DateTimeOffset effectiveAt,
        int? season,
        ScoringConfig config,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var day = GameClock.GameDayOf(effectiveAt);
        var before = await DaySumAsync(db, run.UserId, run.League, day, ScoreKind.Distance, cancellationToken);
        var score = Scores.ForDistance(meters, before, run.League, config);
        if (score.Points <= 0)
        {
            return 0;
        }

        db.ScoreEvents.Add(new ScoreEventEntity
        {
            UserId = run.UserId,
            League = run.League,
            Season = season,
            GameDay = day,
            Kind = ScoreKind.Distance,
            Points = score.Points,
            Basis = Math.Round(score.CountedMeters, 1),
            RunId = run.Id,
            EffectiveAt = effectiveAt,
            VisibleAt = now, // считается уже после границы публичности конца забега
            CreatedAt = now,
        });
        return score.CountedMeters;
    }

    /// <summary>Сумма основ начислений игрока в лиге за игровые сутки одного вида (зачётные сотки или засчитанные метры).</summary>
    private static async Task<double> DaySumAsync(
        AppDbContext db, Guid userId, League league, DateOnly day, ScoreKind kind, CancellationToken cancellationToken) =>
        await db.ScoreEvents
            .Where(e => e.UserId == userId && e.League == league && e.GameDay == day && e.Kind == kind)
            .SumAsync(e => (double?)e.Basis, cancellationToken) ?? 0;
}
