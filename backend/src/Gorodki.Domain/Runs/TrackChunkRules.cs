namespace Gorodki.Domain.Runs;

/// <summary>
/// Что не так с куском: поле (с номером записи) и нарушенное правило. Самих значений нет: координаты не должны попадать
/// ни в ответы, ни в журналы.
/// </summary>
public sealed record ChunkProblem(string Field, string Rule);

/// <summary>Границы для проверки куска.</summary>
/// <param name="RunStartedAtMs">Начало забега по часам телефона.</param>
/// <param name="ClientNowMs">«Сейчас» по часам телефона (момент отправки запроса): точек из будущего по его же часам быть не может.</param>
/// <param name="MaxRunHours">Предел длины забега из конфига.</param>
/// <param name="MaxSeq">Самый большой допустимый номер точки.</param>
/// <param name="MaxPoints">Точек в одном куске не больше.</param>
/// <param name="MaxSamples">Записей движения и шагомера в одном куске — не больше этого (каждого вида).</param>
public sealed record ChunkLimits(long RunStartedAtMs, long ClientNowMs, double MaxRunHours, int MaxSeq, int MaxPoints, int MaxSamples);

/// <summary>
/// Проверка куска забега до записи (PLAN.md, §3.9, слой 1 на сервере). Время сравнивается только с часами того же телефона:
/// если они спешат или отстают, забег всё равно принимается, а сдвиг часов хранится отдельно как признак для доверия.
/// </summary>
public static class TrackChunkRules
{
    /// <summary>Точки чуть раньше старта допустимы: GPS «догоняет» после нажатия «Старт».</summary>
    public const long EarlyToleranceMs = 60_000;

    /// <summary>Запас после предела длины забега: телефон мог записать последние точки, пока закрывал забег.</summary>
    public const long LateToleranceMs = 10 * 60_000;

    /// <summary>Запас «из будущего» относительно момента отправки по тем же часам.</summary>
    public const long FutureToleranceMs = 2 * 60_000;

    /// <summary>Больше проблем не перечисляем: чтобы понять, что кусок испорчен, хватит и этих.</summary>
    public const int MaxProblems = 20;

    public static IReadOnlyList<ChunkProblem> Check(TrackChunk chunk, ChunkLimits limits)
    {
        var problems = new List<ChunkProblem>();

        if (chunk.FirstSeq < 0 || chunk.FirstSeq > limits.MaxSeq)
        {
            problems.Add(new("firstSeq", "seq_limit"));
            return problems;
        }

        if (chunk.Points.Count == 0 || chunk.Points.Count > limits.MaxPoints)
        {
            problems.Add(new("points", "count"));
            return problems;
        }

        if ((long)chunk.FirstSeq + chunk.Points.Count - 1 > limits.MaxSeq)
        {
            problems.Add(new("points", "seq_limit"));
        }

        if (chunk.Motion.Count > limits.MaxSamples)
        {
            problems.Add(new("motion", "count"));
        }

        if (chunk.Steps.Count > limits.MaxSamples)
        {
            problems.Add(new("steps", "count"));
        }

        var windowStart = limits.RunStartedAtMs - EarlyToleranceMs;
        var runEnd = limits.RunStartedAtMs + (long)(limits.MaxRunHours * 3_600_000) + LateToleranceMs;
        var future = limits.ClientNowMs + FutureToleranceMs;

        void CheckTime(string field, long timeMs)
        {
            if (timeMs < windowStart || timeMs > runEnd)
            {
                problems.Add(new(field, "time_window"));
            }
            else if (timeMs > future)
            {
                problems.Add(new(field, "time_future"));
            }
        }

        for (var i = 0; i < chunk.Points.Count && problems.Count < MaxProblems; i++)
        {
            var point = chunk.Points[i];
            var field = $"points[{i}]";
            if (point.Seq != chunk.FirstSeq + i)
            {
                problems.Add(new(field, "seq_order"));
            }

            // Время строго растёт: сотни точек в одну миллисекунду — не GPS.
            if (i > 0 && point.TimeMs <= chunk.Points[i - 1].TimeMs)
            {
                problems.Add(new(field, "time_order"));
            }

            CheckTime(field, point.TimeMs);
        }

        for (var i = 0; i < chunk.Motion.Count && problems.Count < MaxProblems; i++)
        {
            var sample = chunk.Motion[i];
            if (!Enum.IsDefined(sample.Activity))
            {
                problems.Add(new($"motion[{i}]", "activity"));
            }

            CheckTime($"motion[{i}]", sample.TimeMs);
        }

        for (var i = 0; i < chunk.Steps.Count && problems.Count < MaxProblems; i++)
        {
            var sample = chunk.Steps[i];
            if (sample.EndMs < sample.StartMs || sample.Steps < 0)
            {
                problems.Add(new($"steps[{i}]", "steps_invalid"));
            }

            CheckTime($"steps[{i}]", sample.StartMs);
            CheckTime($"steps[{i}]", sample.EndMs);
        }

        if (chunk.SensorsCompleteThroughMs < windowStart || chunk.SensorsCompleteThroughMs > future)
        {
            problems.Add(new("sensorsCompleteThroughMs", "time_window"));
        }

        return problems.Count > MaxProblems ? problems[..MaxProblems] : problems;
    }
}
