using System.Collections.Immutable;

namespace Gorodki.Domain.Territory;

/// <summary>
/// Кто снял уровень с куска в текущем окне. Сравнивается по составу, а не по ссылке: соседние куски с одинаковым
/// состоянием сливаются, и два одинаковых набора обязаны быть равны. Значение по умолчанию — пустой набор.
/// </summary>
public readonly struct AttackerSet : IEquatable<AttackerSet>
{
    private readonly ImmutableArray<Guid> _ids; // по возрастанию, без повторов

    private AttackerSet(ImmutableArray<Guid> ids) => _ids = ids;

    public static AttackerSet Empty => default;

    public int Count => _ids.IsDefault ? 0 : _ids.Length;

    public IReadOnlyList<Guid> Ids => _ids.IsDefault ? [] : _ids;

    public static AttackerSet Of(IEnumerable<Guid> ids) => new([.. ids.Distinct().Order()]);

    public bool Contains(Guid id) => !_ids.IsDefault && _ids.BinarySearch(id) >= 0;

    public AttackerSet With(Guid id) => Contains(id) ? this : Of(Ids.Append(id));

    public bool Equals(AttackerSet other) => Ids.SequenceEqual(other.Ids);

    public override bool Equals(object? obj) => obj is AttackerSet other && Equals(other);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var id in Ids)
        {
            hash.Add(id);
        }

        return hash.ToHashCode();
    }

    /// <summary>Состав — в отпечаток карты (<c>TerritoryMap.StateHash</c>).</summary>
    public override string ToString() => $"[{string.Join(',', Ids)}]";

    public static bool operator ==(AttackerSet left, AttackerSet right) => left.Equals(right);

    public static bool operator !=(AttackerSet left, AttackerSet right) => !left.Equals(right);
}
