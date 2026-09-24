using Gorodki.Domain.Territory;

namespace Gorodki.Api.Features.Territory;

/// <summary>
/// Как публичная проекция откатывала скрытые захваты за один запрос карты (аудит BE-01): сколько точно, сколько запасным
/// путём и почему, сколько тайлов отдано пустыми. Идёт в лог одной строкой — иначе частоту запасного пути в проде не
/// узнать; без тайлов, координат и игроков.
/// </summary>
public sealed class ProjectionStats
{
    private readonly Dictionary<UndoPath, int> _paths = [];
    private readonly Dictionary<string, int> _errors = [];

    /// <summary>Сколько раз захват в тайле откачен точно.</summary>
    public int Exact => _paths.GetValueOrDefault(UndoPath.Exact);

    /// <summary>Сколько раз — запасным путём (откат по граням следа).</summary>
    public int Fallback => _paths.Where(p => p.Key != UndoPath.Exact).Sum(p => p.Value);

    /// <summary>Тайлы, отданные пустыми: запасной путь не сошёлся или запись журнала испорчена.</summary>
    public int EmptyTiles { get; private set; }

    /// <summary>Сколько раз каким путём.</summary>
    public IReadOnlyDictionary<UndoPath, int> Paths => _paths;

    public bool IsEmpty => _paths.Count == 0 && EmptyTiles == 0;

    public void Add(TileProjection projection)
    {
        foreach (var path in projection.Paths)
        {
            _paths[path] = _paths.GetValueOrDefault(path) + 1;
        }

        foreach (var error in projection.Errors)
        {
            var type = error.GetType().Name;
            _errors[type] = _errors.GetValueOrDefault(type) + 1;
        }
    }

    public void AddEmptyTile() => EmptyTiles++;

    /// <summary>«точно 3, запасным путём 1 (missing 1), пустых тайлов 0» — с типами ошибок точного пути, если были.</summary>
    public string Describe()
    {
        var reasons = _paths
            .Where(p => p.Key != UndoPath.Exact)
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key.ToString().ToLowerInvariant()} {p.Value}")
            .Concat(_errors.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} {e.Value}"));
        var fallback = Fallback == 0 ? "0" : $"{Fallback} ({string.Join(", ", reasons)})";
        return $"точно {Exact}, запасным путём {fallback}, пустых тайлов {EmptyTiles}";
    }
}
