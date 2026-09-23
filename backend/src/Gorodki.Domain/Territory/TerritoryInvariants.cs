using Gorodki.Domain.Geo;

namespace Gorodki.Domain.Territory;

/// <summary>
/// Инварианты карты участков (PLAN.md, §7.3, шаг B, пункт 4). Проверяются после каждого захвата в тестах
/// и выборочно — ночной задачей на сервере. Нарушение означает ошибку движка, а не игрока.
/// </summary>
public static class TerritoryInvariants
{
    /// <summary>Допуск на площадь наложений, м²: snap-rounding даёт ровно ноль, допуск — на представление double.</summary>
    public const double OverlapToleranceSquareMeters = 1e-6;

    /// <summary>Возвращает список нарушений; пустой список — всё в порядке.</summary>
    public static IReadOnlyList<string> Check(TerritoryMap map)
    {
        var errors = new List<string>();
        foreach (var tile in map.Tiles)
        {
            var tilePolygon = tile.ToPolygon();
            var pieces = map.ParcelsIn(tile);

            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                var name = $"тайл {tile}, кусок {i}";

                // I1: правильный простой многоугольник на сетке, внутри своего тайла, уровень 1…3.
                if (!piece.Geometry.IsValid)
                {
                    errors.Add($"{name}: неправильная геометрия");
                }

                if (!GeoOps.IsOnGrid(piece.Geometry))
                {
                    errors.Add($"{name}: вершины не на сетке 0,1 м");
                }

                if (piece.Tile != tile)
                {
                    errors.Add($"{name}: записан в чужой тайл {piece.Tile}");
                }

                var outside = GeoOps.Difference(piece.Geometry, tilePolygon).Area;
                if (outside > OverlapToleranceSquareMeters)
                {
                    errors.Add($"{name}: выходит за тайл на {outside:0.###} м²");
                }

                if (piece.State.Level is < 1 or > 3)
                {
                    errors.Add($"{name}: уровень {piece.State.Level}");
                }

                // I6: внутри тайла не должно оставаться осколков.
                if (map.IsSliver(piece.Geometry) && GeoOps.SharedBoundaryLength(piece.Geometry, tilePolygon) == 0)
                {
                    errors.Add($"{name}: осколок {piece.Geometry.Area:0.##} м²");
                }

                // I2: куски не накладываются друг на друга.
                for (var j = i + 1; j < pieces.Count; j++)
                {
                    if (!piece.Geometry.EnvelopeInternal.Intersects(pieces[j].Geometry.EnvelopeInternal))
                    {
                        continue;
                    }

                    var overlap = GeoOps.Intersection(piece.Geometry, pieces[j].Geometry).Area;
                    if (overlap > OverlapToleranceSquareMeters)
                    {
                        errors.Add($"{name} и кусок {j}: наложение {overlap:0.######} м²");
                    }
                }
            }
        }

        return errors;
    }
}
