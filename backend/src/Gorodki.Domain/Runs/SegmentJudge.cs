using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;

namespace Gorodki.Domain.Runs;

/// <summary>Почему точка не принята или след порван. Имена совпадают с <c>TrackIssue</c> в GameCore — причина показывается игроку.</summary>
public enum TrackIssue
{
    /// <summary>Слой 1: точность хуже допустимой.</summary>
    PoorAccuracy,

    /// <summary>Слой 1: точка устарела.</summary>
    StaleFix,

    /// <summary>Слой 1: время пошло назад.</summary>
    TimeWentBackwards,

    /// <summary>Слой 1: скачок, которого не бывает у человека.</summary>
    Teleport,

    /// <summary>Слой 2: средняя скорость за окно выше порога лиги.</summary>
    TooFast,

    /// <summary>Слой 2: датчики говорят «транспорт».</summary>
    Vehicle,

    /// <summary>Слой 2: датчики говорят «велосипед» в лиге «Бег».</summary>
    Cycling,

    /// <summary>Слой 2: движение без шагов.</summary>
    NoSteps,

    /// <summary>Слой 2: длина шага не человеческая.</summary>
    StrideOutOfRange,

    /// <summary>Слой 2: разгон, как у машины.</summary>
    CarLaunch,
}

public enum VerdictKind
{
    /// <summary>Точка принята в текущий отрезок.</summary>
    Accepted,

    /// <summary>Точка отброшена, отрезок продолжается.</summary>
    Ignored,

    /// <summary>Отрезок порван: с этой точки начинается новый. Петля не может охватывать два отрезка (PLAN.md, D16).</summary>
    SegmentBroken,
}

/// <summary>Решение по точке.</summary>
public readonly record struct JudgeVerdict(VerdictKind Kind, TrackIssue? Issue)
{
    public static JudgeVerdict Accepted { get; } = new(VerdictKind.Accepted, null);

    public static JudgeVerdict Ignored(TrackIssue issue) => new(VerdictKind.Ignored, issue);

    public static JudgeVerdict Broken(TrackIssue issue) => new(VerdictKind.SegmentBroken, issue);

    /// <summary>Запись в эталонах <c>contracts/segment-judge.v1.json</c>: <c>accepted</c>, <c>ignored:poorAccuracy</c>, <c>broken:tooFast</c>.</summary>
    public override string ToString() => Kind switch
    {
        VerdictKind.Accepted => "accepted",
        VerdictKind.Ignored => $"ignored:{Camel(Issue)}",
        _ => $"broken:{Camel(Issue)}",
    };

    private static string Camel(TrackIssue? issue)
    {
        var name = issue?.ToString() ?? "unknown";
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

/// <summary>
/// Античит, слои 1 и 2 (PLAN.md, §3.9) — перенос <c>SegmentJudge</c> из GameCore (Swift) строка в строку: сервер повторяет
/// проверки телефона той же версией правил. Совпадение до вердикта проверяют эталоны <c>contracts/segment-judge.v1.json</c>,
/// которые записывает реализация на Swift.
/// </summary>
/// <remarks>Хранит только короткую историю: точки за 5 минут, датчики за 2 минуты. Время — секунды Unix.</remarks>
public sealed class SegmentJudge(LeagueRules rules)
{
    private const double KeepSensorsSeconds = 130;

    private readonly List<TrackPoint> _points = [];
    private readonly List<MotionSample> _motion = [];
    private readonly List<StepSample> _pedometer = [];

    public LeagueRules Rules { get; } = rules;

    public void Record(MotionSample sample)
    {
        _motion.Add(sample);
        Prune(Seconds(sample.TimeMs));
    }

    public void Record(StepSample sample)
    {
        _pedometer.Add(sample);
        Prune(Seconds(sample.EndMs));
    }

    /// <summary>Проверяет новую точку. <paramref name="now"/> — «сейчас» для проверки свежести (на сервере — время самой точки).</summary>
    public JudgeVerdict Judge(TrackPoint point, double now)
    {
        var time = Seconds(point.TimeMs);

        // Слой 1 — сама точка.
        if (point.AccuracyMeters > Rules.MaxAccuracyMeters || point.AccuracyMeters < 0)
        {
            return JudgeVerdict.Ignored(TrackIssue.PoorAccuracy);
        }

        if (now - time > Rules.MaxFixAgeSeconds)
        {
            return JudgeVerdict.Ignored(TrackIssue.StaleFix);
        }

        if (_points.Count > 0)
        {
            var last = _points[^1];
            var dt = time - Seconds(last.TimeMs);
            if (dt <= 0)
            {
                return JudgeVerdict.Ignored(TrackIssue.TimeWentBackwards);
            }

            if (Distance(last, point) / dt > Rules.TeleportMetersPerSecond)
            {
                return BreakSegment(point, TrackIssue.Teleport);
            }
        }

        _points.Add(point);
        Prune(time);

        // Слой 2 — отрезки.
        return SegmentIssue(time) is { } issue ? BreakSegment(point, issue) : JudgeVerdict.Accepted;
    }

    // Слой 2.

    private TrackIssue? SegmentIssue(double now)
    {
        foreach (var limit in Rules.SpeedLimits)
        {
            if (AverageSpeed(limit.WindowSeconds, now) is { } speed && speed * 3.6 > limit.MaxKilometersPerHour)
            {
                return TrackIssue.TooFast;
            }
        }

        if (Rules.VehicleSeconds is { } vehicleSeconds && ContinuousDuration(MotionActivity.Automotive, now) >= vehicleSeconds)
        {
            return TrackIssue.Vehicle;
        }

        if (Rules.CyclingSeconds is { } cyclingSeconds && ContinuousDuration(MotionActivity.Cycling, now) >= cyclingSeconds)
        {
            return TrackIssue.Cycling;
        }

        if (Rules.VehicleShare is { } share
            && Share(MotionActivity.Automotive, share.WindowSeconds, now) >= share.MinShare
            && AverageSpeed(share.SpeedWindowSeconds, now) is { } shareSpeed
            && shareSpeed * 3.6 > share.MinKilometersPerHour)
        {
            return TrackIssue.Vehicle;
        }

        if (Rules.CarLaunch is { } launch && IsCarLaunch(launch, now))
        {
            return TrackIssue.CarLaunch;
        }

        return StepIssue(now);
    }

    /// <summary>Средняя скорость за последние <paramref name="window"/> секунд (путь / время) или null, если истории ещё мало.</summary>
    private double? AverageSpeed(double window, double now)
    {
        if (_points.Count == 0 || now - Seconds(_points[0].TimeMs) < window)
        {
            return null;
        }

        var from = now - window;
        var distance = 0.0;
        double? start = null;
        for (var index = 1; index < _points.Count; index++)
        {
            if (Seconds(_points[index].TimeMs) < from)
            {
                continue;
            }

            var previous = _points[index - 1];
            if (Seconds(previous.TimeMs) < from)
            {
                start = Seconds(previous.TimeMs);
            }

            distance += Distance(previous, _points[index]);
        }

        var duration = now - (start ?? from);
        return duration > 0 ? distance / duration : null;
    }

    /// <summary>Сколько секунд подряд (до <paramref name="now"/>) датчики сообщают этот вид движения.</summary>
    private double ContinuousDuration(MotionActivity activity, double now)
    {
        if (_motion.Count == 0 || _motion[^1].Activity != activity)
        {
            return 0;
        }

        var since = Seconds(_motion[^1].TimeMs);
        for (var index = _motion.Count - 2; index >= 0; index--)
        {
            if (_motion[index].Activity != activity)
            {
                break;
            }

            since = Seconds(_motion[index].TimeMs);
        }

        return now - since;
    }

    /// <summary>Доля времени за окно, когда датчики сообщали этот вид движения.</summary>
    private double Share(MotionActivity activity, double window, double now)
    {
        var from = now - window;
        var total = 0.0;
        for (var index = 0; index < _motion.Count; index++)
        {
            var sample = _motion[index];
            var end = index + 1 < _motion.Count ? Seconds(_motion[index + 1].TimeMs) : now;
            var overlap = Math.Min(end, now) - Math.Max(Seconds(sample.TimeMs), from);
            if (sample.Activity == activity && overlap > 0)
            {
                total += overlap;
            }
        }

        return total / window;
    }

    /// <summary>Скорость выросла на Δ за короткое время и достигла порога — так разгоняется машина, а не велосипед.</summary>
    private bool IsCarLaunch(CarLaunchRule rule, double now)
    {
        if (InstantSpeed(_points.Count - 1) is not { } current || current * 3.6 < rule.ReachingKilometersPerHour)
        {
            return false;
        }

        for (var index = _points.Count - 2; index >= 0; index--)
        {
            if (now - Seconds(_points[index].TimeMs) > rule.WithinSeconds)
            {
                break;
            }

            if (InstantSpeed(index) is { } earlier && (current - earlier) * 3.6 >= rule.DeltaKilometersPerHour)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Мгновенная скорость: от GPS, а если её нет — по соседней точке.</summary>
    private double? InstantSpeed(int index)
    {
        if (index < 0 || index >= _points.Count)
        {
            return null;
        }

        if (_points[index].SpeedMetersPerSecond is { } speed && speed >= 0)
        {
            return speed;
        }

        if (index == 0)
        {
            return null;
        }

        var dt = Seconds(_points[index].TimeMs) - Seconds(_points[index - 1].TimeMs);
        return dt > 0 ? Distance(_points[index - 1], _points[index]) / dt : null;
    }

    /// <summary>Шаги: движение без шагов и нечеловеческая длина шага. Неизвестное число шагов (null) ничего не решает.</summary>
    private TrackIssue? StepIssue(double now)
    {
        if (Rules.NoStepsWindowSeconds is { } window
            && Steps(window, now) is 0
            && AverageSpeed(window, now) is { } speed
            && speed >= Rules.NoStepsMinSpeed)
        {
            return TrackIssue.NoSteps;
        }

        if (Rules.StrideMeters is [var shortest, var longest]
            && Steps(Rules.StrideWindowSeconds, now) is { } steps and > 0
            && AverageSpeed(Rules.StrideWindowSeconds, now) is { } strideSpeed)
        {
            var stride = strideSpeed * Rules.StrideWindowSeconds / steps;
            if (stride < shortest || stride > longest)
            {
                return TrackIssue.StrideOutOfRange;
            }
        }

        return null;
    }

    /// <summary>Шаги за окно, если шагомер покрыл его целиком и знает число шагов; иначе null.</summary>
    private int? Steps(double window, double now)
    {
        var from = now - window;
        var covering = _pedometer.Where(s => Seconds(s.EndMs) > from && Seconds(s.StartMs) < now).ToList();
        if (covering.Count == 0
            || covering.Min(s => Seconds(s.StartMs)) > from
            || covering.Max(s => Seconds(s.EndMs)) < now - 1)
        {
            return null;
        }

        var total = 0;
        foreach (var sample in covering)
        {
            if (sample.Steps is not { } steps)
            {
                return null;
            }

            var start = Seconds(sample.StartMs);
            var end = Seconds(sample.EndMs);
            var length = end - start;
            var overlap = Math.Min(end, now) - Math.Max(start, from);
            total += length > 0 ? (int)Math.Round(steps * overlap / length, MidpointRounding.AwayFromZero) : steps;
        }

        return total;
    }

    // История.

    /// <summary>Разрыв: история начинается заново с этой точки.</summary>
    private JudgeVerdict BreakSegment(TrackPoint point, TrackIssue issue)
    {
        _points.Clear();
        _points.Add(point);
        return JudgeVerdict.Broken(issue);
    }

    private void Prune(double now)
    {
        var keepPoints = (Rules.SpeedLimits.Count > 0 ? Rules.SpeedLimits.Max(l => l.WindowSeconds) : 300) + 10;
        var index = _points.FindIndex(p => now - Seconds(p.TimeMs) <= keepPoints);
        if (index > 1)
        {
            _points.RemoveRange(0, index - 1);
        }

        var motionIndex = _motion.FindIndex(s => now - Seconds(s.TimeMs) <= KeepSensorsSeconds);
        if (motionIndex > 1)
        {
            _motion.RemoveRange(0, motionIndex - 1);
        }

        _pedometer.RemoveAll(s => now - Seconds(s.EndMs) > KeepSensorsSeconds);
    }

    private static double Seconds(long timeMs) => timeMs / 1000.0;

    private static double Distance(TrackPoint a, TrackPoint b) => Geodesy.Distance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
}
