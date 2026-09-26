using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Загрузка набора в базу (osm-pipeline.md, «Поток данных», шаг 7): одной транзакцией рядом с прежним, прежний не трогается.
/// Повторная загрузка того же набора (тот же отпечаток) ничего не делает; другой набор под тем же номером — ошибка.
/// Действующим набор становится только новой версией игрового конфига (<c>osm.setVersion</c>), не здесь.
/// </summary>
public static class SetImporter
{
    public enum Outcome
    {
        Imported,
        AlreadyImported,
    }

    /// <summary>Переменная окружения со строкой подключения — не из репозитория и не из аргументов (они видны в журнале оболочки).</summary>
    public const string ConnectionVariable = "GORODKI_OSM_DB";

    public static async Task<Outcome> ImportAsync(AppDbContext db, OsmSetFileContent set, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Две загрузки одного номера одновременно: вторая ждёт первую, а не падает на ключе посреди вставки. Пространство
        // блокировок 5 — конвейер OSM (игрок — 1, туман — 2, устройство — 3, срез — 4, тайлы — 100 + лига; список —
        // docs/architecture/jobs.md, «Пространства блокировок»).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(5, {set.Version})", cancellationToken);
        var existing = await db.OsmSets.AsNoTracking().SingleOrDefaultAsync(s => s.Version == set.Version, cancellationToken);
        if (existing is not null)
        {
            return existing.Fingerprint == set.Fingerprint
                ? Outcome.AlreadyImported
                : throw new InvalidOperationException(
                    $"Набор {set.Version} уже загружен с другим содержимым (отпечаток {existing.Fingerprint[..12]}…, а у файла {set.Fingerprint[..12]}…). " +
                    "Набор после загрузки не меняется — соберите новый с другим номером.");
        }

        var data = set.Data;
        db.OsmSets.Add(new OsmSetEntity
        {
            Version = set.Version,
            Fingerprint = set.Fingerprint,
            SourceSha256 = set.Source.Sha256,
            SourceTimestamp = set.Source.ReplicationTimestamp,
            Metadata = set.Metadata.ToJsonString(),
            FrameMinX = data.Frame.MinX,
            FrameMinY = data.Frame.MinY,
            FrameMaxX = data.Frame.MaxX,
            FrameMaxY = data.Frame.MaxY,
            PlayZone = data.PlayZone,
            ReachableCells = data.ReachableCells,
            BuiltAt = set.BuiltAt,
            ImportedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        foreach (var chunk in data.Masks.Chunk(2_000))
        {
            db.Masks.AddRange(chunk.Select(m => new MaskEntity
            {
                SetVersion = set.Version,
                Kind = m.Kind,
                TileX = m.Tile.X,
                TileY = m.Tile.Y,
                Geometry = m.Geometry,
            }));
            await SaveAndForgetAsync(db, cancellationToken);
        }

        foreach (var chunk in data.Land.Chunk(2_000))
        {
            db.LandZones.AddRange(chunk.Select(m => new LandZoneEntity
            {
                SetVersion = set.Version,
                Kind = m.Kind,
                TileX = m.Tile.X,
                TileY = m.Tile.Y,
                Geometry = m.Geometry,
            }));
            await SaveAndForgetAsync(db, cancellationToken);
        }

        db.ReachableTiles.AddRange(data.Reachable.Select(t => new ReachableTileEntity
        {
            SetVersion = set.Version,
            TileX = t.Key.X,
            TileY = t.Key.Y,
            Bits = FogTileCodec.Compress(t.Value),
            CellCount = t.Value.Count,
        }));
        await SaveAndForgetAsync(db, cancellationToken);

        foreach (var district in data.Districts)
        {
            var entity = new DistrictEntity
            {
                SetVersion = set.Version,
                Key = district.Key,
                Kind = district.Kind,
                Name = district.Name,
                OsmId = district.OsmId,
                Proposal = district.Proposal,
                Geometry = district.Geometry as MultiPolygon ?? GeoOps.Factory.CreateMultiPolygon([.. GeoOps.Polygons(district.Geometry)]),
                AreaWithoutMasks = district.AreaWithoutMasks,
                ReachableCells = district.CellCount,
            };
            db.Districts.Add(entity);
            await db.SaveChangesAsync(cancellationToken); // нужен id района для его клеток
            db.DistrictTiles.AddRange(district.Tiles.Select(t => new DistrictTileEntity
            {
                DistrictId = entity.Id,
                TileX = t.Key.X,
                TileY = t.Key.Y,
                Bits = FogTileCodec.Compress(t.Value),
                CellCount = t.Value.Count,
            }));
            await SaveAndForgetAsync(db, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Outcome.Imported;
    }

    private static async Task SaveAndForgetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }
}
