using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Segments;

/// <summary>Точка засчитанного пути: UTM 34N в метрах, время — по часам телефона, мс Unix.</summary>
public readonly record struct PathPoint(double X, double Y, long TimeMs);

/// <summary>Засчитанный проход отрезка: моменты старта и финиша (часы телефона, мс Unix), интерполированные между точками.</summary>
public readonly record struct SegmentEffort(long StartMs, long FinishMs)
{
    /// <summary>Время прохода, мс — единственное, что о проходе видят другие (короны и таблица).</summary>
    public long ElapsedMs => FinishMs - StartMs;
}

/// <summary>
/// Линия отрезка от старта к финишу в UTM 34N (метры): ворота — круги вокруг её концов, коридор — полоса вокруг неё.
/// </summary>
public sealed class SegmentCourse
{
    private readonly Coordinate[] _points;
    private readonly double[] _along;

    /// <exception cref="ArgumentException">Меньше двух точек или нулевая длина.</exception>
    public SegmentCourse(IReadOnlyList<Coordinate> line)
    {
        if (line.Count < 2)
        {
            throw new ArgumentException("У линии отрезка меньше двух точек.", nameof(line));
        }

        _points = [.. line.Select(c => new Coordinate(c.X, c.Y))];
        _along = new double[_points.Length];
        for (var i = 1; i < _points.Length; i++)
        {
            _along[i] = _along[i - 1] + _points[i - 1].Distance(_points[i]);
        }

        Length = _along[^1];
        if (Length <= 0)
        {
            throw new ArgumentException("У линии отрезка нулевая длина.", nameof(line));
        }

        Envelope = new Envelope(_points);
    }

    public Coordinate Start => _points[0];

    public Coordinate Finish => _points[^1];

    /// <summary>Длина линии, метры.</summary>
    public double Length { get; }

    /// <summary>Рамка линии (без коридора).</summary>
    public Envelope Envelope { get; }

    /// <summary>Расстояние от точки до линии и место ближайшей к ней точки линии — метры от старта по линии.</summary>
    public (double Distance, double Along) Locate(double x, double y)
    {
        var best = (Distance: double.MaxValue, Along: 0.0);
        for (var i = 0; i + 1 < _points.Length; i++)
        {
            var (u, distance) = Nearest(x, y, _points[i].X, _points[i].Y, _points[i + 1].X, _points[i + 1].Y);
            if (distance < best.Distance)
            {
                best = (distance, _along[i] + (u * (_along[i + 1] - _along[i])));
            }
        }

        return best;
    }

    /// <summary>Ближайшая к (x; y) точка отрезка A→B: доля пути от A (0…1) и расстояние до неё.</summary>
    internal static (double U, double Distance) Nearest(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        var squared = (dx * dx) + (dy * dy);
        var u = squared == 0 ? 0 : Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / squared, 0, 1);
        double px = ax + (u * dx) - x, py = ay + (u * dy) - y;
        return (u, Math.Sqrt((px * px) + (py * py)));
    }
}

/// <summary>
/// Засчитывание прохода отрезка (PLAN.md, §3.13: «ворота старта (25 м), затем ворота финиша, по порядку; коридор 30 м; время
/// интерполируется; все точки прошли правила лиги; „бег“ быстрее 30 км/ч на отрезке не засчитывается»). Чистая функция:
/// на входе — засчитанный судьёй путь забега (<see cref="Stretches"/>), на выходе — проходы с моментами старта и финиша.
/// </summary>
/// <remarks>
/// <para>
/// <b>Правила лиги</b> — уже в пути: он состоит из кусков без разрывов судьи (<see cref="JudgedPath"/>: точки разрыва и 30 с
/// перед ними не в счёт, точки с плохой точностью пропускаются). Проход не может перешагнуть ни разрыв судьи, ни разрыв
/// точек — соседние точки дальше <see cref="SegmentRules.MaxGapMeters"/>.
/// </para>
/// <para>
/// <b>Старт</b> — место пути, ближайшее к началу линии, за последний заход в ворота старта (не дальше
/// <see cref="SegmentRules.GateRadiusMeters"/>); время — интерполяцией между соседними точками. Кто вернулся к старту и
/// начал заново, стартует со второго захода. <b>Финиш</b> — место, ближайшее к концу линии, за первый после старта заход в
/// ворота финиша. Между ними каждая точка — в коридоре (<see cref="SegmentRules.CorridorMeters"/>), иначе старт сброшен;
/// путь идёт вдоль всей линии — места точек на линии без пропусков длиннее разрыва точек (срезать изгиб нельзя). Обратное
/// направление не засчитывается: ворота финиша без старта перед ними ничего не значат. После финиша поиск идёт дальше —
/// проходов в одном забеге может быть несколько.
/// </para>
/// </remarks>
public static class SegmentEffortDetector
{
    /// <summary>
    /// Засчитанный судьёй путь забега кусками без разрывов (<see cref="JudgedPath.Segments"/>), точки — в UTM 34N.
    /// Одиночные точки (кусок из одной точки) в путь не входят.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PathPoint>> Stretches(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts)
    {
        var stretches = new List<IReadOnlyList<PathPoint>>();
        List<PathPoint>? current = null;
        var lastSeq = int.MinValue;
        foreach (var (from, to) in JudgedPath.Segments(points, verdicts))
        {
            if (current is null || from.Seq != lastSeq)
            {
                current = [ToPath(from)];
                stretches.Add(current);
            }

            current.Add(ToPath(to));
            lastSeq = to.Seq;
        }

        return stretches;
    }

    /// <summary>Проходы отрезка в пути забега лиги <paramref name="league"/> — по порядку старта.</summary>
    public static IReadOnlyList<SegmentEffort> Detect(
        SegmentCourse course, IReadOnlyList<IReadOnlyList<PathPoint>> stretches, League league, SegmentRules rules)
    {
        var area = new Envelope(course.Envelope);
        area.ExpandBy(rules.CorridorMeters);
        var efforts = new List<SegmentEffort>();
        foreach (var stretch in stretches)
        {
            if (stretch.Any(p => area.Contains(p.X, p.Y)))
            {
                DetectIn(course, stretch, league, rules, efforts);
            }
        }

        return efforts;
    }

    /// <summary>Место пути, ближайшее к воротам: расстояние, время (интерполяция) и номер точки, которой кончается шаг.</summary>
    private readonly record struct GatePass(double Distance, double TimeMs, double X, double Y, int StepEnd);

    private static void DetectIn(SegmentCourse course, IReadOnlyList<PathPoint> path, League league, SegmentRules rules, List<SegmentEffort> efforts)
    {
        var gate = rules.GateRadiusMeters;
        var maxGap = rules.MaxGapMeters.For(league);
        GatePass? start = null;
        var inStartGate = false;
        for (var i = 0; i < path.Count; i++)
        {
            var b = path[i];
            var a = b; // у первой точки и после разрыва шаг — сама точка
            if (i > 0 && Gap(path[i - 1], b) <= maxGap)
            {
                a = path[i - 1];
            }
            else if (i > 0)
            {
                // Разрыв точек: где был игрок, неизвестно.
                start = null;
                inStartGate = false;
            }

            if (start is { } begun && Pass(course.Finish, a, b, i) is var reached && reached.Distance <= gate)
            {
                // Финиш — самое близкое к концу линии место за этот заход в ворота; дальше заход тянется, пока шаги задевают
                // ворота, а точка, с которой начинается шаг, ещё в коридоре.
                var finish = reached;
                var j = i;
                while (j + 1 < path.Count
                    && Gap(path[j], path[j + 1]) <= maxGap
                    && course.Locate(path[j].X, path[j].Y).Distance <= rules.CorridorMeters
                    && Pass(course.Finish, path[j], path[j + 1], j + 1) is var next
                    && next.Distance <= gate)
                {
                    if (next.Distance < finish.Distance)
                    {
                        finish = next;
                    }

                    j++;
                }

                if (Counted(course, path, begun, finish, league, rules) is { } effort)
                {
                    efforts.Add(effort);
                }

                start = null;
                inStartGate = false;
                i = j;
                continue;
            }

            if (course.Locate(b.X, b.Y).Distance > rules.CorridorMeters)
            {
                // Вышел из коридора — начатый проход не засчитывается.
                start = null;
                inStartGate = false;
                continue;
            }

            var near = Pass(course.Start, a, b, i);
            if (near.Distance <= gate)
            {
                // Новый заход в ворота старта заменяет прежний старт; в пределах захода — ближайшее к началу линии место.
                if (!inStartGate || start is null || near.Distance <= start.Value.Distance)
                {
                    start = near;
                }

                inStartGate = true;
            }
            else
            {
                inStartGate = false;
            }
        }
    }

    /// <summary>Проход от старта до финиша, если он засчитывается: время больше нуля, путь вдоль всей линии, скорость лиги.</summary>
    private static SegmentEffort? Counted(
        SegmentCourse course, IReadOnlyList<PathPoint> path, GatePass start, GatePass finish, League league, SegmentRules rules)
    {
        var effort = new SegmentEffort((long)Math.Round(start.TimeMs), (long)Math.Round(finish.TimeMs));
        if (effort.ElapsedMs <= 0)
        {
            return null;
        }

        if (rules.MaxSpeedMetersPerSecond(league) is { } maxSpeed && course.Length / (effort.ElapsedMs / 1000.0) > maxSpeed)
        {
            return null; // «бег» быстрее 30 км/ч — не бег
        }

        // Путь — вдоль всей линии: места точек на линии (старт, точки между, финиш) идут без пропусков длиннее разрыва точек.
        var along = new List<double> { course.Locate(start.X, start.Y).Along, course.Locate(finish.X, finish.Y).Along };
        for (var k = start.StepEnd; k < finish.StepEnd; k++)
        {
            along.Add(course.Locate(path[k].X, path[k].Y).Along);
        }

        along.Sort();
        var maxGap = rules.MaxGapMeters.For(league);
        for (var k = 1; k < along.Count; k++)
        {
            if (along[k] - along[k - 1] > maxGap)
            {
                return null;
            }
        }

        return effort;
    }

    private static GatePass Pass(Coordinate center, PathPoint a, PathPoint b, int stepEnd)
    {
        var (u, distance) = SegmentCourse.Nearest(center.X, center.Y, a.X, a.Y, b.X, b.Y);
        return new GatePass(
            distance, a.TimeMs + (u * (b.TimeMs - a.TimeMs)), a.X + (u * (b.X - a.X)), a.Y + (u * (b.Y - a.Y)), stepEnd);
    }

    private static double Gap(PathPoint a, PathPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static PathPoint ToPath(TrackPoint point)
    {
        var (x, y) = Utm34.Forward(point.Latitude, point.Longitude);
        return new PathPoint(x, y, point.TimeMs);
    }
}
