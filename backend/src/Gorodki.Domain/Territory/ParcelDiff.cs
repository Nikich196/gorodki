namespace Gorodki.Domain.Territory;

/// <summary>Что записать в хранилище по тайлу после захвата.</summary>
/// <param name="Kept">Идентификаторы кусков, которые не изменились (та же геометрия и то же состояние).</param>
/// <param name="Removed">Идентификаторы кусков, которых больше нет.</param>
/// <param name="Added">Новые куски.</param>
public sealed record ParcelDiff(IReadOnlyList<long> Kept, IReadOnlyList<long> Removed, IReadOnlyList<Parcel> Added)
{
    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;

    /// <summary>
    /// Разница между кусками тайла до и после захвата. Пишется только она: неизменные куски сохраняют свои идентификаторы,
    /// в базе не копятся «мёртвые» строки, а журнал откатов остаётся маленьким (PLAN.md, §7.3).
    /// </summary>
    public static ParcelDiff Compute(IReadOnlyList<(long Id, Parcel Parcel)> before, IReadOnlyList<Parcel> after)
    {
        var remaining = after.ToList();
        var kept = new List<long>();
        var removed = new List<long>();
        foreach (var (id, parcel) in before)
        {
            var normalized = parcel.Geometry.Normalized();
            var match = remaining.FindIndex(p => p.State == parcel.State && p.Geometry.Normalized().EqualsExact(normalized));
            if (match >= 0)
            {
                kept.Add(id);
                remaining.RemoveAt(match);
            }
            else
            {
                removed.Add(id);
            }
        }

        return new ParcelDiff(kept, removed, remaining);
    }
}
