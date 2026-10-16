using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Duels;
using Gorodki.Domain.Fog;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Duels;

/// <summary>
/// Живой счёт дуэли (PLAN.md, §3.14) — только видимые данные (§3.16, docs/guides/egor-server.md, раздел 4). Окно —
/// [начало; конец) дуэли: захват — по времени петли (§3.4: «по времени замыкания, а не обработки»), забег — по началу по
/// часам сервера (как дистанция в книге очков и сезонный туман). По настоящим кускам земли (<c>db.Parcels</c>) не считается
/// ничего: «захваченная площадь» — это «взятое» заявок, а не земля сейчас.
/// </summary>
/// <remarks>
/// <para>
/// <b>Два взгляда.</b> <c>own: false</c> — как видит посторонний: захват — с границы публичности его применения (как карта и
/// очки: <see cref="TerritoryReader.PublicHorizon"/>, у демо-аккаунта — сразу), дистанция и туман — по забегам, чей конец уже
/// публичен (визиты посчитаны, <c>runs.visits_processed_at</c>). Так считается счёт соперника, лидер для уведомлений и итог.
/// <c>own: true</c> — игрок смотрит свой счёт: «взятое» своих захватов он видит сразу (как в итоге заявки), свой туман —
/// как только забег его открыл. Своя дистанция — тоже после подсчёта визитов: засчитанный путь сервер считает только тогда.
/// </para>
/// <para>
/// Сравнение «кто впереди» на экране игрока — его свежий счёт против публичного счёта соперника: ничего скрытого о сопернике
/// в нём нет. Наоборот тоже: соперник видит только публичный счёт игрока. Поэтому лидер для «Входящих» и итог считаются
/// только по публичному счёту обоих (<see cref="DuelReferee"/>).
/// </para>
/// </remarks>
public sealed class DuelScores(AppDbContext db, GameConfigStore configs, IDuelSegments segments)
{
    /// <summary>
    /// Площадь клетки тумана в Бресте, м² (сторона ≈5,87 м на широте центра города). Забег хранит только число новых клеток
    /// (<c>runs.fog_new_cells</c>), без тайлов; в пределах города площадь клетки меняется меньше чем на 0,2 %, и у обеих
    /// сторон она одна и та же.
    /// </summary>
    public static readonly double FogCellSquareMeters = Math.Pow(FogGrid.CellSizeMeters(BrestLatitude), 2);

    /// <summary>Широта центра Бреста — та же, что у тестовых прогулок и образца тумана.</summary>
    private const double BrestLatitude = 52.0976;

    /// <summary>
    /// Счёт стороны в единицах метрики (м², м или мс), округлён до 0,1; <c>null</c> — дуэль ещё не начата или у
    /// <c>segmentTime</c> нет ни одного прохода.
    /// </summary>
    /// <param name="own">Игрок смотрит свой счёт (см. «Два взгляда»); <c>false</c> — как видит посторонний.</param>
    public async Task<double?> ScoreAsync(DuelEntity duel, Guid playerId, bool own, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (duel.StartsAt is not { } from || duel.EndsAt is not { } to)
        {
            return null;
        }

        double? score = duel.Metric switch
        {
            DuelMetric.CapturedArea => await CapturedAsync(duel, playerId, own, from, to, now, cancellationToken),
            DuelMetric.Distance => (await RunsAsync(duel, playerId, from, to, cancellationToken))
                .Where(r => r.VisitsProcessedAt is not null)
                .Sum(r => r.AcceptedMeters ?? 0),
            DuelMetric.ExploredArea => (await RunsAsync(duel, playerId, from, to, cancellationToken))
                .Where(r => r.FogNewCells is not null && (own || r.VisitsProcessedAt is not null))
                .Sum(r => r.FogNewCells!.Value) * FogCellSquareMeters,
            DuelMetric.SegmentTime when duel.SegmentId is { } segment =>
                await segments.BestTimeMsAsync(segment, playerId, duel.League, from, to, publicOnly: !own, cancellationToken),
            _ => null,
        };
        return DuelRules.Shown(score);
    }

    /// <summary>
    /// «Взятое» применённых и не откаченных захватов с петлёй в окне. Посторонний видит захват с границы публичности его
    /// применения — той же, что у карты (<see cref="TerritoryReader.HiddenJournal"/>); откаченный захват не считается.
    /// </summary>
    private async Task<double> CapturedAsync(
        DuelEntity duel, Guid playerId, bool own, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        var horizon = TerritoryReader.PublicHorizon(now, delay);
        var atOnce = own || await db.Users.AnyAsync(u => u.Id == playerId && u.Role == UserRole.Demo, cancellationToken);
        return await db.Captures.AsNoTracking()
            .Where(c => c.UserId == playerId && c.League == duel.League && c.Status == CaptureStatus.Applied && c.RolledBackAt == null
                && c.EffectiveAt >= from && c.EffectiveAt < to
                && (atOnce || c.AppliedAt <= horizon))
            .SumAsync(c => (double?)c.AreaSquareMeters, cancellationToken) ?? 0;
    }

    private sealed record RunRow(DateTimeOffset StartedAt, long ClockSkewMs, double? AcceptedMeters, int? FogNewCells, DateTimeOffset? VisitsProcessedAt);

    /// <summary>
    /// Живые забеги игрока в лиге дуэли, начатые в окне по часам сервера (начало по телефону минус сдвиг его часов, как у
    /// визитов и тумана). Сдвиг — у каждого забега свой, поэтому база отбирает с запасом в сутки, а точно — здесь.
    /// </summary>
    private async Task<List<RunRow>> RunsAsync(
        DuelEntity duel, Guid playerId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var rows = await db.Runs.AsNoTracking()
            .Where(r => r.UserId == playerId && r.League == duel.League && r.Source == RunSource.Live
                && r.StartedAt >= from.AddDays(-1) && r.StartedAt < to.AddDays(1))
            .Select(r => new RunRow(r.StartedAt, r.ClockSkewMs, r.AcceptedMeters, r.FogNewCells, r.VisitsProcessedAt))
            .ToListAsync(cancellationToken);
        return rows.Where(r => r.StartedAt.AddMilliseconds(-r.ClockSkewMs) is var start && start >= from && start < to).ToList();
    }
}
