using Gorodki.Domain.Geo;

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

    /// <summary>
    /// Строки точного отката по этой разнице (<see cref="ParcelSwap"/>): какие строки хранилища захват удалил — с их
    /// контуром, как он лежал в хранилище, — и какие вставил вместо них. Тот же помощник зовут сервер и тестовое хранилище:
    /// строки журнала совпадают с тем, что на самом деле записано. <c>null</c> — контур удалённого куска нельзя сохранить
    /// без потерь (<see cref="ParcelSwap.Encode"/>): точного отката в этом тайле не будет, проекция пойдёт запасным путём.
    /// </summary>
    /// <param name="before">Куски тайла до захвата с номерами их строк — те же, что ушли в <see cref="Compute"/>.</param>
    /// <param name="addedIds">
    /// Номера строк, которые хранилище дало кускам <see cref="Added"/>, в том же порядке. На сервере они известны только
    /// после сохранения (identity): до него это нули, и точный откат не найдёт куски по номерам.
    /// </param>
    public ParcelSwap? Swap(TileKey tile, IReadOnlyList<(long Id, Parcel Parcel)> before, IReadOnlyList<long> addedIds)
    {
        if (addedIds.Count != Added.Count)
        {
            throw new ArgumentException($"Номеров {addedIds.Count}, а новых кусков {Added.Count}.", nameof(addedIds));
        }

        var replaced = new List<JournalParcel>();
        foreach (var (id, parcel) in before.Where(b => Removed.Contains(b.Id)))
        {
            if (ParcelSwap.Encode(parcel.Geometry) is not { } geometry)
            {
                return null;
            }

            replaced.Add(new JournalParcel(id, parcel.State, geometry));
        }

        return new ParcelSwap(tile, replaced, [.. Added.Select((parcel, i) => new JournalParcel(addedIds[i], parcel.State, null))]);
    }
}
