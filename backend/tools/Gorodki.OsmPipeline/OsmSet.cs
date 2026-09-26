using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using NetTopologySuite.Geometries;

namespace Gorodki.OsmPipeline;

/// <summary>Прямоугольник тайлов UTM 1×1 км, который покрывает рамку конвейера. За ним строк масок нет.</summary>
public sealed record TileRange(int MinX, int MinY, int MaxX, int MaxY)
{
    public IEnumerable<TileKey> Tiles()
    {
        for (var x = MinX; x <= MaxX; x++)
        {
            for (var y = MinY; y <= MaxY; y++)
            {
                yield return new TileKey(x, y);
            }
        }
    }

    public bool Contains(TileKey tile) => tile.X >= MinX && tile.X <= MaxX && tile.Y >= MinY && tile.Y <= MaxY;

    /// <summary>Тайлы, которые задевает рамка в градусах (края рамки в UTM — кривые, поэтому берутся точки по всем краям).</summary>
    public static TileRange Of(Frame frame)
    {
        const int Steps = 64;
        var points = new List<(double E, double N)>();
        for (var i = 0; i <= Steps; i++)
        {
            var t = (double)i / Steps;
            var lon = frame.West + ((frame.East - frame.West) * t);
            var lat = frame.South + ((frame.North - frame.South) * t);
            points.Add(Utm34.Forward(frame.South, lon));
            points.Add(Utm34.Forward(frame.North, lon));
            points.Add(Utm34.Forward(lat, frame.West));
            points.Add(Utm34.Forward(lat, frame.East));
        }

        var min = TileKey.Of(points.Min(p => p.E), points.Min(p => p.N));
        var max = TileKey.Of(points.Max(p => p.E), points.Max(p => p.N));
        return new TileRange(min.X, min.Y, max.X, max.Y);
    }
}

/// <summary>Кусок маски: один простой многоугольник внутри одного тайла UTM (как у участков).</summary>
public sealed record MaskPiece(MaskKind Kind, TileKey Tile, Polygon Geometry);

/// <summary>Кусок слоя ценности земли (§3.5) — так же по тайлам.</summary>
public sealed record LandPiece(LandKind Kind, TileKey Tile, Polygon Geometry);

/// <summary>Город, район, Арена или квартал: контур, площадь без масок и «достижимые» клетки внутри.</summary>
public sealed record DistrictData(
    string Key,
    DistrictKind Kind,
    string Name,
    long? OsmId,
    bool Proposal,
    Geometry Geometry,
    double AreaWithoutMasks,
    SortedDictionary<FogTileKey, FogTileBits> Tiles)
{
    public int CellCount => Tiles.Values.Sum(t => t.Count);
}

/// <summary>Содержимое набора <c>osm-set-N</c> в памяти.</summary>
public sealed class OsmSetData
{
    public required TileRange Frame { get; init; }

    public required PlayZone PlayZone { get; init; }

    public required IReadOnlyList<MaskPiece> Masks { get; init; }

    public required IReadOnlyList<LandPiece> Land { get; init; }

    public required SortedDictionary<FogTileKey, FogTileBits> Reachable { get; init; }

    public required IReadOnlyList<DistrictData> Districts { get; init; }

    /// <summary>Что в наборе выключено или неполно — пишется в metadata.json и в отчёт.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public int ReachableCells => Reachable.Values.Sum(t => t.Count);
}
