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
    /// <summary>
    /// Как проекция откатывала скрытые захваты при последнем чтении — для лога и тестов. Счёт — за одно чтение, а не за
    /// сервис: «мои данные» (<c>AccountExport</c>) читают карту в одной области по лигам несколько раз.
    /// </summary>
    public ProjectionStats Projections { get; private set; } = new();

    /// <summary>
    /// Для тестов: вызывается в транзакции чтения после версий и списка скрытых захватов, перед чтением кусков
    /// (проверка «захват, записанный посреди чтения, не виден»).
    /// </summary>
    public Func<CancellationToken, Task>? BeforeParcelsRead { get; set; }

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
        Projections = new ProjectionStats();
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

        if (BeforeParcelsRead is { } hook)
        {
            await hook(cancellationToken);
        }

        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.League == league && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
            .ToListAsync(cancellationToken);

        // Журнал скрытых захватов — сразу для всех тайлов запроса и до расчёта: сбой базы остаётся ошибкой сервера (5xx),
        // а расчёт проекции (ProjectTile) базы уже не касается.
        var journals = await CaptureJournal.LoadHiddenAsync(
            db, [.. changed.SelectMany(c => c.Pending.Select(h => (h.CaptureId, c.Tile)))], cancellationToken);

        var tiles = new List<(TileKey Tile, long Version, List<(ParcelState State, Polygon Geometry)> Pieces)>();
        foreach (var (tile, version, pending) in changed)
        {
            var stored = parcels.Where(p => p.TileX == tile.X && p.TileY == tile.Y).ToList();
            var pieces = pending.Count == 0
                ? stored.Select(p => (CaptureProcessor.ToParcel(p).State, p.Geometry)).ToList()
                : ProjectTile(
                    tile,
                    [.. stored.Select(p => new ProjectedParcel(p.Id, CaptureProcessor.ToParcel(p)))],
                    [.. pending.OrderByDescending(h => h.AppliedSeq).Select(h => journals[(h.CaptureId, tile)])],
                    rules,
                    Projections,
                    logger);
            tiles.Add((tile, version, pieces));
        }

        var owners = tiles.SelectMany(t => t.Pieces.Select(p => p.State.OwnerId)).Distinct().ToList();
        var colors = await db.Users.AsNoTracking()
            .Where(u => owners.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.ColorIndex, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (!Projections.IsEmpty)
        {
            // Одна строка на запрос: как часто точный откат уходит в запасной путь (и почему) — иначе в проде не узнать.
            // Без тайлов, координат и игроков: сам лог не должен выдавать, где бегали. Уровень — от содержания
            // (ProjectionStats.Level): карту перечитывают часто, и строка на каждое точное чтение была бы шумом.
            logger.Log(Projections.Level, "Публичная проекция: {Projections}", Projections.Describe());
        }

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
    /// Тайл таким, каким его видят остальные: недавние чужие захваты откатываются в памяти — от новых к старым
    /// (<see cref="ExactUndo.Project"/>). Точно, по строкам журнала: удалённые захватом строки кусков возвращаются как
    /// лежали — до вершины и с теми же номерами, вставленные убираются, визиты владельцев, засчитанные за это время,
    /// переносятся на прежние куски. Если точно нельзя (вставленные куски с тех пор переписали — например, свой захват
    /// зрителя, — строк нет, они испорчены), — запасной путь: откат по граням следа с переносом визитов, только там, где
    /// земля и сейчас такая, какой её оставил захват (свои более поздние изменения зрителя остаются).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Номера строк переходят от захвата к захвату: вставленное старым захватом новый удалил, точный откат нового вернул
    /// его с тем же номером — и старый откатывается точно. Всё читается в той же транзакции REPEATABLE READ, что версии и
    /// список скрытых захватов: захват, записанный посреди чтения, не виден ни в кусках, ни в журнале. Правила земли —
    /// действующего конфига, как у визитов (<see cref="VisitProcessor"/>). Что остаётся — docs/architecture/territory-map.md.
    /// </para>
    /// <para>
    /// Это чистый расчёт — журнал уже загружен (<see cref="CaptureJournal.LoadHiddenAsync"/>), — и он не бросает ничего,
    /// кроме отмены: любая ошибка — пустой тайл до раскрытия и строка в журнале ошибок сервера. Ошибки бывают не только
    /// свои (движок не сошёлся, запись журнала испорчена): запасной путь — это NTS и весь движок, и неожиданное исключение
    /// оттуда без этого стало бы 500 каждому, кто смотрит тайл, до конца скрытия — а 500 ровно на тайлах со скрытым
    /// захватом ещё и показал бы, где он. Лучше пустой тайл на 20 минут.
    /// </para>
    /// </remarks>
    /// <param name="newestFirst">Скрытые от зрителя захваты тайла — от новых к старым.</param>
    /// <param name="stats">Сюда — каким путём откачен каждый захват или что тайл отдан пустым.</param>
    public static List<(ParcelState State, Polygon Geometry)> ProjectTile(
        TileKey tile,
        IReadOnlyList<ProjectedParcel> stored,
        IReadOnlyList<HiddenTileChange> newestFirst,
        TerritoryRules rules,
        ProjectionStats stats,
        ILogger logger)
    {
        try
        {
            var projection = ExactUndo.Project(tile, stored, newestFirst, rules, new SliverSettings());
            stats.Add(projection);
            return [.. projection.Pieces.Select(p => (p.Parcel.State, p.Parcel.Geometry))];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            stats.AddEmptyTile();
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
