namespace Gorodki.Domain.Runs;

/// <summary>Диапазон номеров точек <c>FirstSeq…LastSeq</c> включительно.</summary>
public readonly record struct SeqRange(int FirstSeq, int LastSeq)
{
    /// <summary>
    /// Склеивает диапазоны: соседние (<c>…99</c> и <c>100…</c>) и пересекающиеся сливаются в один.
    /// Так телефону проще понять, что у сервера уже есть.
    /// </summary>
    public static IReadOnlyList<SeqRange> Merge(IEnumerable<SeqRange> ranges)
    {
        var merged = new List<SeqRange>();
        foreach (var range in ranges.OrderBy(r => r.FirstSeq))
        {
            if (merged.Count > 0 && (long)range.FirstSeq <= (long)merged[^1].LastSeq + 1)
            {
                merged[^1] = merged[^1] with { LastSeq = Math.Max(merged[^1].LastSeq, range.LastSeq) };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>
    /// Последний номер непрерывного начала следа (от точки 0 без дыр) или −1, если точки 0 ещё нет.
    /// Обрабатывается только это начало: за дырой может оказаться что угодно.
    /// </summary>
    public static int ContiguousPrefixEnd(IEnumerable<SeqRange> received) =>
        Merge(received) is [{ FirstSeq: 0 } first, ..] ? first.LastSeq : -1;

    /// <summary>Каких номеров от 0 до <paramref name="lastSeq"/> нет среди <paramref name="received"/> — их телефону нужно дослать.</summary>
    public static IReadOnlyList<SeqRange> Missing(IEnumerable<SeqRange> received, int lastSeq)
    {
        var missing = new List<SeqRange>();
        var next = 0;
        foreach (var range in Merge(received))
        {
            if (range.FirstSeq > lastSeq)
            {
                break;
            }

            if (range.FirstSeq > next)
            {
                missing.Add(new SeqRange(next, range.FirstSeq - 1));
            }

            next = Math.Max(next, range.LastSeq + 1);
        }

        if (next <= lastSeq)
        {
            missing.Add(new SeqRange(next, lastSeq));
        }

        return missing;
    }
}
