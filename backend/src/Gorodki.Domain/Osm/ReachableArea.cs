using Gorodki.Domain.Fog;

namespace Gorodki.Domain.Osm;

/// <summary>
/// «Достижимая» площадь на сетке тумана (PLAN.md, §3.10, §7.3): клетки G22, центр которых лежит в полосе 25 м вокруг
/// пешеходных путей OSM в границах города (или района) минус маски. Готовит конвейер OSM, сервер только читает.
/// </summary>
/// <remarks>
/// Сетка та же, что у тумана игрока (<see cref="FogGrid"/>, тайлы z14, <see cref="FogTileBits"/>), поэтому процент
/// считается пословно, без перевода координат: <c>% = popcount(explored &amp; reachable) / popcount(reachable)</c>.
/// Открытое вне «достижимого» (двор без дорожек в OSM, пригород) идёт в гектары, но не в процент — он не больше 100.
/// </remarks>
public sealed class ReachableArea
{
    private readonly SortedDictionary<FogTileKey, FogTileBits> _tiles = new();

    public ReachableArea(IEnumerable<KeyValuePair<FogTileKey, FogTileBits>> tiles)
    {
        foreach (var (key, bits) in tiles)
        {
            if (_tiles.ContainsKey(key))
            {
                throw new ArgumentException($"Тайл {key} встречается дважды.", nameof(tiles));
            }

            _tiles[key] = bits;
            TotalCells += bits.Count;
        }
    }

    /// <summary>Знаменатель процента: сколько клеток достижимо.</summary>
    public int TotalCells { get; }

    public IReadOnlyDictionary<FogTileKey, FogTileBits> Tiles => _tiles;

    /// <summary>Какая доля «достижимого» открыта в слое игрока. Тайлы тумана вне «достижимого» не читаются вовсе.</summary>
    public ExploredShare ShareOf(IReadOnlyDictionary<FogTileKey, FogTileBits> explored)
    {
        var opened = 0;
        foreach (var (key, bits) in _tiles)
        {
            if (explored.TryGetValue(key, out var layer))
            {
                opened += layer.CountAnd(bits);
            }
        }

        return new ExploredShare(opened, TotalCells);
    }

    /// <summary>
    /// «Всего» — объединение слоёв, а не сумма: <c>popcount((foot | bike) &amp; reachable)</c> (osm-pipeline.md, «Как
    /// считается % Бреста»). Сумма процентов «Пешком» и «Вело» могла бы дать больше 100.
    /// </summary>
    public ExploredShare ShareOfUnion(IEnumerable<IReadOnlyDictionary<FogTileKey, FogTileBits>> layers)
    {
        var union = new Dictionary<FogTileKey, FogTileBits>();
        foreach (var layer in layers)
        {
            foreach (var (key, bits) in layer)
            {
                if (!_tiles.ContainsKey(key))
                {
                    continue;
                }

                if (!union.TryGetValue(key, out var merged))
                {
                    merged = new FogTileBits();
                    union[key] = merged;
                }

                merged.UnionWith(bits);
            }
        }

        return ShareOf(union);
    }
}

/// <summary>Сколько «достижимых» клеток открыто из скольких.</summary>
public readonly record struct ExploredShare(int OpenedCells, int ReachableCells)
{
    /// <summary>Процент, 0–100; без «достижимого» — 0.</summary>
    public double Percent => ReachableCells == 0 ? 0 : 100.0 * OpenedCells / ReachableCells;
}
