using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Tests.Geo;

/// <summary>
/// Построители следов для тестов. Все координаты — метры от «начала» в Бресте,
/// которое совпадает с углом четырёх тайлов: так легко проверять переходы через края тайлов.
/// Никаких реальных домов и маршрутов — только синтетика.
/// </summary>
public static class TestGeometry
{
    /// <summary>Угол четырёх тайлов в Бресте (UTM 34N).</summary>
    public const double OriginX = 684_000;
    public const double OriginY = 5_775_000;

    public static Coordinate At(double x, double y) => new(OriginX + x, OriginY + y);

    /// <summary>
    /// След, который обходит многоугольник по вершинам с точками каждые <paramref name="step"/> метров
    /// (как GPS раз в секунду на бегу) и останавливается чуть не доходя до старта.
    /// </summary>
    public static List<Coordinate> Trail(IReadOnlyList<(double X, double Y)> vertices, double step = 5, bool close = true)
    {
        var trail = new List<Coordinate>();
        var count = close ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var (x1, y1) = vertices[i];
            var (x2, y2) = vertices[(i + 1) % vertices.Count];
            var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            var segments = Math.Max(1, (int)Math.Ceiling(length / step));
            for (var s = 0; s < segments; s++)
            {
                var t = (double)s / segments;
                trail.Add(At(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t));
            }
        }

        if (!close)
        {
            var (lx, ly) = vertices[^1];
            trail.Add(At(lx, ly));
        }
        else
        {
            // Последняя точка — в 3 м от старта: петля замкнута «почти», как в жизни.
            var (sx, sy) = vertices[0];
            var (nx, ny) = vertices[1];
            var length = Math.Sqrt((nx - sx) * (nx - sx) + (ny - sy) * (ny - sy));
            trail.Add(At(sx - (nx - sx) / length * 3, sy - (ny - sy) / length * 3));
        }

        return trail;
    }

    /// <summary>Вершины прямоугольника против часовой стрелки.</summary>
    public static (double X, double Y)[] Rectangle(double x, double y, double width, double height) =>
        [(x, y), (x + width, y), (x + width, y + height), (x, y + height)];

    /// <summary>Прямоугольник как геометрия (для масок и проверок).</summary>
    public static Polygon RectanglePolygon(double x, double y, double width, double height) =>
        GeoOps.Factory.CreatePolygon(
        [
            At(x, y), At(x + width, y), At(x + width, y + height), At(x, y + height), At(x, y),
        ]);
}
