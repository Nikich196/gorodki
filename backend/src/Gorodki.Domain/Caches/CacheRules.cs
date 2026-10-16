using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Caches;

/// <summary>Тайник — для поиска находок: номер и место (координаты на телефон не уходят никогда).</summary>
public readonly record struct CacheSpot(Guid Id, double Latitude, double Longitude);

/// <summary>
/// Находки тайников (PLAN.md, §3.12): «найти — пройти в 30 м в валидном забеге». Валидный путь — тот же, что открывает
/// туман и засчитывает визиты (<see cref="JudgedPath"/>): точки с плохой точностью пропускаются, разрыв по правилам
/// отрезков рвёт путь, последние 30 с перед разрывом не считаются. Из машины тайник не найти.
/// </summary>
public static class CacheRules
{
    /// <summary>Тайник найден, если засчитанный путь прошёл не дальше этого от него, м (§3.12).</summary>
    public const double FindRadiusMeters = 30;

    /// <summary>
    /// Какие тайники забег нашёл и когда: номер тайника → время по часам телефона (мс Unix) — самая ранняя точка пути
    /// в <paramref name="radius"/> метрах от тайника. Отрезок между соседними засчитанными точками тоже считается (при
    /// редких точках велосипеда путь мог пройти мимо тайника между ними): время — в ближайшей к тайнику точке отрезка,
    /// по линейной интерполяции. Расстояния — в UTM 34N, как всё на карте.
    /// </summary>
    public static IReadOnlyDictionary<Guid, long> Finds(
        IReadOnlyList<TrackPoint> points,
        IReadOnlyList<JudgeVerdict> verdicts,
        IReadOnlyCollection<CacheSpot> caches,
        double radius = FindRadiusMeters)
    {
        var found = new Dictionary<Guid, long>();
        if (caches.Count == 0)
        {
            return found;
        }

        var spots = caches.Select(c => (c.Id, Position: Utm34.Forward(c.Latitude, c.Longitude))).ToList();
        var excluded = JudgedPath.Excluded(points, verdicts);
        (double X, double Y, long TimeMs)? previous = null;
        for (var i = 0; i < points.Count && found.Count < spots.Count; i++)
        {
            if (verdicts[i].Kind == VerdictKind.Ignored)
            {
                continue; // плохая точность — точка не в счёт, но путь не рвёт
            }

            if (excluded[i])
            {
                previous = null; // разрыв: отрезок через него не тянем
                continue;
            }

            var (x, y) = Utm34.Forward(points[i].Latitude, points[i].Longitude);
            var current = (X: x, Y: y, points[i].TimeMs);
            foreach (var (id, (cx, cy)) in spots)
            {
                if (found.ContainsKey(id))
                {
                    continue;
                }

                // Сначала отрезок от прошлой точки (он раньше по времени), потом сама точка.
                if (previous is { } from && Closest(from, current, cx, cy) is var (distance, timeMs) && distance <= radius)
                {
                    found[id] = timeMs;
                }
                else if (Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) <= radius)
                {
                    found[id] = current.TimeMs;
                }
            }

            previous = current;
        }

        return found;
    }

    /// <summary>Ближайшая к точке (cx, cy) точка отрезка: расстояние и время в ней (интерполяция между концами).</summary>
    private static (double Distance, long TimeMs) Closest(
        (double X, double Y, long TimeMs) from, (double X, double Y, long TimeMs) to, double cx, double cy)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((cx - from.X) * dx) + ((cy - from.Y) * dy)) / lengthSquared, 0, 1);
        double px = from.X + (t * dx), py = from.Y + (t * dy);
        var timeMs = from.TimeMs + (long)Math.Round((to.TimeMs - from.TimeMs) * t);
        return (Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy))), timeMs);
    }
}
