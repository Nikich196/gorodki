using System.Data;
using System.Security.Cryptography;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Territory;

/// <summary>Кто смотрит карту.</summary>
/// <param name="UserId">Игрок; свои захваты он видит сразу.</param>
/// <param name="Immediate">Видит всё без задержки — только администратор (PLAN.md, §3.16).</param>
public sealed record TerritoryViewer(Guid? UserId, bool Immediate);

/// <summary>
/// Чтение земли по тайлам для карты: версии тайлов, куски, угасание «при чтении» (PLAN.md, §3.3, §7.3) и публичная
/// проекция с задержкой (§3.16): чужой захват остальные видят только через 20 минут — до этого на его месте земля такая,
/// какой была до него (по журналу захватов). Иначе карта показывала бы, где конкретный человек находится прямо сейчас.
/// </summary>
/// <remarks>
/// <para>
/// Скрытый захват не должен выдавать себя ничем. Поэтому зрителю отдаётся <b>видимая версия</b> тайла: настоящая минус
/// число скрытых от него захватов в тайле (каждый захват поднимает версию тайла ровно на 1). Пока захват скрыт, видимая
/// версия не меняется — тайл приходит как «без изменений»; когда он раскрывается, версия растёт. Видимая версия никогда
/// не убывает: откат и удаление аккаунта сами поднимают настоящую версию.
/// </para>
/// <para>
/// Захваты раскрываются пачками — на границах по 5 минут (<see cref="RevealStep"/>): так скрыта и минута захвата.
/// Номера кусков — от содержимого (<see cref="ContentId"/>): кусок, собранный проекцией, не отличить от настоящего.
/// Всё читается одним снимком базы — захват, записанный посреди чтения, не даст «провала» версии.
/// </para>
/// <para>
/// Без задержки видны свои изменения и изменения демо-аккаунта (§11, показ: «захват — /live обновился»); всё сразу видит
/// только администратор.
/// </para>
/// </remarks>
public sealed class TerritoryReader(AppDbContext db, GameConfigStore configs, TimeProvider time, ILogger<TerritoryReader> logger)
{
    /// <summary>Задержка публичной проекции по умолчанию (<c>privacy.publicEventDelayMinutes</c> в игровом конфиге, §3.16).</summary>
    public static readonly TimeSpan PublicDelay = TimeSpan.FromMinutes(Gorodki.Domain.Config.GameConfig.Default.Privacy.PublicEventDelayMinutes);

    /// <summary>Захваты раскрываются пачками на таких границах.</summary>
    public static readonly TimeSpan RevealStep = TimeSpan.FromMinutes(5);

    /// <summary>До какого момента применённые чужие изменения уже публичны: «сейчас» минус задержка, вниз до 5 минут.</summary>
    public static DateTimeOffset PublicHorizon(DateTimeOffset now, TimeSpan delay)
    {
        var edge = (now - delay).UtcTicks;
        return new DateTimeOffset(edge - (edge % RevealStep.Ticks), TimeSpan.Zero);
    }

    public async Task<TerritoryResponse> ReadAsync(
        League league,
        IReadOnlyList<(TileKey Tile, long? KnownVersion)> requested,
        TerritoryViewer viewer,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var config = (await configs.GetCurrentAsync(cancellationToken)).Rules;
        var rules = config.Territory.ToRules();
        var horizon = PublicHorizon(now, TimeSpan.FromMinutes(config.Privacy.PublicEventDelayMinutes));
        int minX = requested.Min(t => t.Tile.X), maxX = requested.Max(t => t.Tile.X);
        int minY = requested.Min(t => t.Tile.Y), maxY = requested.Max(t => t.Tile.Y);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var versions = (await db.TileVersions.AsNoTracking()
                .Where(v => v.League == league && v.TileX >= minX && v.TileX <= maxX && v.TileY >= minY && v.TileY <= maxY)
                .ToListAsync(cancellationToken))
            .ToDictionary(v => new TileKey(v.TileX, v.TileY), v => v.Version);
        var hidden = viewer.Immediate ? [] : await HiddenCapturesAsync(league, viewer.UserId, horizon, minX, maxX, minY, maxY, cancellationToken);

        var changed = new List<(TileKey Tile, long Version, List<HiddenCapture> Pending)>();
        var unchanged = new List<TileRef>();
        foreach (var (tile, known) in requested)
        {
            var pending = hidden.Where(h => h.TileX == tile.X && h.TileY == tile.Y).ToList();
            var visible = versions.GetValueOrDefault(tile) - pending.Count;
            if (known == visible)
            {
                unchanged.Add(new TileRef(tile.X, tile.Y));
            }
            else
            {
                changed.Add((tile, visible, pending));
            }
        }

        if (changed.Count == 0)
        {
            return new TerritoryResponse(league, [], unchanged);
        }

        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.League == league && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
            .ToListAsync(cancellationToken);

        var tiles = new List<(TileKey Tile, long Version, List<(ParcelState State, Polygon Geometry)> Pieces)>();
        foreach (var (tile, version, pending) in changed)
        {
            var stored = parcels.Where(p => p.TileX == tile.X && p.TileY == tile.Y).ToList();
            var pieces = pending.Count == 0
                ? stored.Select(p => (CaptureProcessor.ToParcel(p).State, p.Geometry)).ToList()
                : await ProjectAsync(tile, stored, pending, rules, cancellationToken);
            tiles.Add((tile, version, pieces));
        }

        var owners = tiles.SelectMany(t => t.Pieces.Select(p => p.State.OwnerId)).Distinct().ToList();
        var colors = await db.Users.AsNoTracking()
            .Where(u => owners.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.ColorIndex, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = tiles
            .Select(t => new TileTerritory(
                t.Tile.X,
                t.Tile.Y,
                t.Version,
                t.Pieces
                    .Where(p => colors.ContainsKey(p.State.OwnerId)) // из журнала мог вернуться кусок уже удалённого аккаунта
                    .Select(p => ToView(t.Tile, p.State, p.Geometry, colors[p.State.OwnerId], viewer, rules, now))
                    .OfType<ParcelView>()
                    .OrderBy(p => p.Id)
                    .ToList()))
            .ToList();
        return new TerritoryResponse(league, result, unchanged);
    }

    private sealed record HiddenCapture(Guid CaptureId, int TileX, int TileY, long AppliedSeq);

    /// <summary>
    /// Чужие захваты в этих тайлах, ещё не публичные (применены позже <paramref name="horizon"/>). Не в счёт: свои,
    /// откаченные (их земли уже нет) и захваты демо-аккаунта (на показе они видны сразу).
    /// </summary>
    private async Task<List<HiddenCapture>> HiddenCapturesAsync(
        League league, Guid? viewerId, DateTimeOffset horizon, int minX, int maxX, int minY, int maxY, CancellationToken cancellationToken)
    {
        var rows = await db.CaptureJournal.AsNoTracking()
            .Where(j => j.League == league && j.AppliedAt > horizon
                && j.TileX >= minX && j.TileX <= maxX && j.TileY >= minY && j.TileY <= maxY)
            .Join(
                db.Captures,
                j => j.CaptureId,
                c => c.Id,
                (j, c) => new { j.CaptureId, j.TileX, j.TileY, c.UserId, c.AppliedSeq, c.RolledBackAt })
            .Where(r => r.UserId != viewerId
                && r.RolledBackAt == null
                && !db.Users.Any(u => u.Id == r.UserId && u.Role == UserRole.Demo))
            .ToListAsync(cancellationToken);
        return rows.Select(r => new HiddenCapture(r.CaptureId, r.TileX, r.TileY, r.AppliedSeq ?? 0)).ToList();
    }

    /// <summary>
    /// Тайл таким, каким его видят остальные: недавние чужие захваты откатываются в памяти — от новых к старым, только там,
    /// где земля и сейчас такая, какой её оставил захват или какой её сделали с тех пор визиты владельцев (свои более
    /// поздние изменения зрителя остаются).
    /// </summary>
    /// <remarks>
    /// Визиты забега засчитываются, когда публичен его конец, а захват из того же забега может быть применён позже (забег
    /// из офлайна) — и тогда визиты автора ложатся на ещё скрытую взятую землю; жертва так же пробегает по треснувшей части.
    /// Такие визиты переносятся на прежнюю землю (<see cref="TerritoryMap.Restore"/> с <c>replayVisits</c>): иначе земля
    /// «после захвата» осталась бы в проекции как есть и выдала бы захват раньше 20 минут. Правила земли — действующего
    /// конфига, как у визитов (<see cref="VisitProcessor"/>).
    /// </remarks>
    private async Task<List<(ParcelState State, Polygon Geometry)>> ProjectAsync(
        TileKey tile, List<ParcelEntity> stored, List<HiddenCapture> pending, TerritoryRules rules, CancellationToken cancellationToken)
    {
        try
        {
            var map = new TerritoryMap(rules, new SliverSettings());
            map.Load(stored.Select(CaptureProcessor.ToParcel));
            foreach (var capture in pending.OrderByDescending(h => h.AppliedSeq))
            {
                var changes = (await CaptureJournal.LoadAsync(db, capture.CaptureId, cancellationToken)).Where(c => c.Tile == tile).ToList();
                map.Restore(changes, replayVisits: true);
            }

            return map.ParcelsIn(tile).Select(p => (p.State, p.Geometry)).ToList();
        }
        catch (Exception e) when (e is TerritoryEngineException or TopologyException or FormatException)
        {
            // Лучше пустой тайл на 20 минут, чем показать, где человек сейчас. FormatException — испорченная запись
            // журнала (TWKB): без неё здесь весь запрос карты отвечал бы 500.
            logger.LogError(e, "Публичная проекция тайла {Tile} не собралась — тайл отдан пустым до раскрытия", tile);
            return [];
        }
    }

    /// <summary>Кусок для карты с учётом угасания; null — земля потеряна и «призрак» уже исчез.</summary>
    private static ParcelView? ToView(
        TileKey tile, ParcelState state, Polygon geometry, short colorIndex, TerritoryViewer viewer, TerritoryRules rules, DateTimeOffset now)
    {
        var level = Decay.EffectiveLevel(state, now, rules);
        var ghost = level == 0 && Decay.IsGhost(state, now, rules);
        if (level == 0 && !ghost)
        {
            return null;
        }

        // Точное время чужого визита — это «был здесь в 18:42», а щит (+12 ч) и осада (+24 ч) до миллисекунды выдают
        // минуту захвата. Остальным хватает часа для визита и 10 минут для щита и осады.
        var foreign = !viewer.Immediate && state.OwnerId != viewer.UserId;
        var lastVisit = state.LastVisitAt.ToUnixTimeMilliseconds();
        var shieldUntil = state.ShieldUntil?.ToUnixTimeMilliseconds();
        var siegeUntil = state.SiegeUntil?.ToUnixTimeMilliseconds();
        if (foreign)
        {
            lastVisit -= lastVisit % 3_600_000;
            shieldUntil = CeilTo(shieldUntil, 600_000);
            siegeUntil = CeilTo(siegeUntil, 600_000);
        }

        var view = new ParcelView(
            0,
            state.OwnerId,
            colorIndex,
            (short)level,
            ghost,
            lastVisit,
            shieldUntil,
            siegeUntil,
            LatLon(geometry.ExteriorRing),
            [.. geometry.InteriorRings.Select(LatLon)]);
        return view with { Id = ContentId(tile, view) };
    }

    private static long? CeilTo(long? ms, long step) => ms is { } value ? value + ((step - (value % step)) % step) : null;

    /// <summary>
    /// Номер куска — от того, что видит зритель (тайл, владелец, уровень, времена, контур): тот же кусок — тот же номер,
    /// любое видимое изменение — новый. Номер собранного проекцией куска так не отличить от настоящего.
    /// Положительное число до 2^53 — точно представимо и в JavaScript (<c>/live</c>).
    /// </summary>
    public static long ContentId(TileKey tile, ParcelView view)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(tile.X);
            writer.Write(tile.Y);
            writer.Write(view.OwnerId.ToByteArray());
            writer.Write(view.Level);
            writer.Write(view.Ghost);
            writer.Write(view.LastVisitAtMs);
            writer.Write(view.ShieldUntilMs ?? -1);
            writer.Write(view.SiegeUntilMs ?? -1);
            foreach (var ring in view.Holes.Prepend(view.Exterior))
            {
                writer.Write(ring.Count);
                foreach (var value in ring)
                {
                    writer.Write(value);
                }
            }
        }

        var hash = SHA256.HashData(buffer.ToArray());
        return BitConverter.ToInt64(hash, 0) & 0x001F_FFFF_FFFF_FFFF;
    }

    /// <summary>Кольцо из UTM 34N в широту и долготу; 7 знаков после запятой — около 1 см.</summary>
    internal static IReadOnlyList<double> LatLon(LineString ring)
    {
        var result = new double[ring.NumPoints * 2];
        for (var i = 0; i < ring.NumPoints; i++)
        {
            var point = ring.GetCoordinateN(i);
            var (latitude, longitude) = Utm34.Inverse(point.X, point.Y);
            result[2 * i] = Math.Round(latitude, 7);
            result[(2 * i) + 1] = Math.Round(longitude, 7);
        }

        return result;
    }
}
