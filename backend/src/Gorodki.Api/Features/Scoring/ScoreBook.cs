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
/// <para>
/// <b>Итог сезона.</b> Сезон и сутки начисления — по времени петли или началу забега (§3.4), а видно оно позже: захват — с
/// границы публичности применения (петля последних минут применяется до 3 ч спустя), дистанция — после границы конца забега.
/// Поэтому в момент смены сезона его очки ещё приходят, и итог окончателен только в момент закрытия (<see cref="ClosesAt"/>):
/// это начисления, видимые на него. Начисление, ставшее видимым позже (забег из офлайна — до 7 дней, захват после простоя
/// сервера), остаётся в книге с номером своего сезона, но в итог не входит — итог закрытого сезона больше не меняется.
/// </para>
/// </remarks>
public static class ScoreBook
{
    /// <summary>
    /// Запас после конца сезона до его закрытия (<see cref="ClosesAt"/>). Петля последней секунды сезона применяется, пока ей
    /// не больше 3 ч от прихода данных (<c>CaptureProcessor.StaleAfter</c>), и видна ещё через задержку и шаг раскрытия
    /// (20 + 5 минут): 3 ч 25 мин — с запасом до 04:00 первого дня следующего сезона. Визиты и дистанция забега, кончившегося
    /// перед полуночью, считаются через 20–25 минут после его конца.
    /// </summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromHours(4);

    /// <summary>Начисления, которые уже видны всем (<c>visible_at ≤ now</c>). Всё, что показывается другим, — только отсюда.</summary>
    public static IQueryable<ScoreEventEntity> Visible(AppDbContext db, DateTimeOffset now) =>
        db.ScoreEvents.AsNoTracking().Where(e => e.VisibleAt <= now);

    /// <summary>
    /// Когда сезон закрыт и его итог окончателен: конец сезона (начало следующего) плюс <see cref="CloseGrace"/> — 04:00 по
    /// Минску первого дня следующего сезона. <c>null</c> — такого сезона нет или он последний и идёт до показа.
    /// </summary>
    public static DateTimeOffset? ClosesAt(SeasonCalendar calendar, int season) =>
        season >= 0 && season < calendar.Seasons.Count && calendar.EndOf(calendar.Seasons[season]) is { } end ? end + CloseGrace : null;

    /// <summary>
    /// Очки сезона по игрокам лиги — сумма начислений, видимых на <paramref name="now"/> (рейтинги, суточный срез E7). У
    /// закрытого сезона (<paramref name="now"/> позже <see cref="ClosesAt"/>) — видимых на момент закрытия: его итог больше не
    /// меняется и совпадает с <see cref="FinalTotalsAsync"/>. Игроки без начислений в ответ не входят.
    /// </summary>
    public static async Task<Dictionary<Guid, int>> SeasonTotalsAsync(
        AppDbContext db, SeasonCalendar calendar, League league, int season, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = ClosesAt(calendar, season) is { } closesAt && closesAt < now ? closesAt : now;
        var rows = await Visible(db, cutoff)
            .Where(e => e.League == league && e.Season == season)
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Points = g.Sum(e => e.Points) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.UserId, r => r.Points);
    }

    /// <summary>
    /// Итог сезона — для снимков, которые делаются один раз (Зал славы E8, итоговый срез E7 за последний день сезона):
    /// начисления, видимые на момент закрытия (<see cref="ClosesAt"/>). <c>null</c> — сезон ещё не закрыт (или последний и
    /// идёт до показа): снимок делать рано, его очки ещё приходят.
    /// </summary>
    public static async Task<Dictionary<Guid, int>?> FinalTotalsAsync(
        AppDbContext db, SeasonCalendar calendar, League league, int season, DateTimeOffset now, CancellationToken cancellationToken) =>
        ClosesAt(calendar, season) is { } closesAt && now >= closesAt
            ? await SeasonTotalsAsync(db, calendar, league, season, closesAt, cancellationToken)
            : null;

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
