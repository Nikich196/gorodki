using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Territory;

/// <summary>
/// Приватные зоны игрока (PLAN.md, §3.16: «при первом „Старте“ — предложение сделать это место приватной зоной (400 м)»;
/// §3.3: визиты в них не засчитываются). Зона — круг вокруг точки; здесь — их общая площадь в UTM 34N.
/// </summary>
public static class PrivacyZones
{
    /// <summary>Сторон у круга на четверть окружности: 16 — отклонение от окружности 400 м меньше 2 м.</summary>
    private const int QuadrantSegments = 16;

    /// <summary>Объединение кругов вокруг центров (UTM 34N); <c>null</c> — зон нет.</summary>
    public static Geometry? Area(IReadOnlyCollection<Coordinate> centers, double radiusMeters)
    {
        if (centers.Count == 0)
        {
            return null;
        }

        return GeoOps.UnionAll(centers.Select(c => GeoOps.Factory.CreatePoint(c).Buffer(radiusMeters, QuadrantSegments)));
    }
}
