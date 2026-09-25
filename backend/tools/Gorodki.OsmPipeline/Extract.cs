using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Gorodki.Domain.Geo;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Шаги osmium (osm-pipeline.md, «Поток данных», шаги 1–3): вырезка рамки Бреста стратегией <c>smart</c>, отбор по темам,
/// экспорт в GeoJSONSeq с типом и id объекта и с журналом ошибок сборки многоугольников; границы — по id из полной выгрузки.
/// </summary>
public static class Extract
{
    /// <summary>Темы: файл и выражения <c>osmium tags-filter</c>.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Filters)> Themes(PipelineParams parameters) =>
    [
        ("lines", ["w/highway", "w/railway"]),
        ("areas", [.. AreaRules(parameters).Select(Filter)]),
        ("buildings", ["wr/building", "wr/amenity=university"]),
        ("memorials", ["nwr/historic", "nwr/memorial"]),
    ];

    private static IEnumerable<string> AreaRules(PipelineParams parameters) =>
        new[]
        {
            parameters.Areas.Water, parameters.Areas.RailAreas, parameters.Areas.Military, parameters.Areas.Cemetery,
            parameters.Areas.PedestrianAreas, parameters.Areas.LowValueLand,
        }.SelectMany(list => list).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary><c>ключ=значение</c> → <c>wr/ключ=значение</c>, <c>ключ=*</c> → <c>wr/ключ</c>.</summary>
    public static string Filter(string rule) => rule.EndsWith("=*", StringComparison.Ordinal) ? $"wr/{rule[..^2]}" : $"wr/{rule}";

    public static void Run(PipelineParams parameters, string pbf, string work, string osmium)
    {
        Directory.CreateDirectory(work);
        var brest = Path.Combine(work, "brest.osm.pbf");
        Osmium(osmium, "extract", "-s", "smart", "-S", "types=multipolygon,boundary", "-b", parameters.Frame.OsmiumBox, pbf, "-O", "-o", brest);

        var ids = new List<string> { $"r{parameters.CityRelationId}", $"r{parameters.CountryRelationId}" };
        ids.AddRange(parameters.DistrictRelationIds.Select(id => $"r{id}"));
        var borders = Path.Combine(work, "borders.osm.pbf");
        Osmium(osmium, ["getid", "-r", pbf, .. ids, "-O", "-o", borders]);
        Export(osmium, borders, Path.Combine(work, "borders"));

        foreach (var (name, filters) in Themes(parameters))
        {
            var themePbf = Path.Combine(work, $"{name}.osm.pbf");
            Osmium(osmium, ["tags-filter", brest, .. filters, "-O", "-o", themePbf]);
            Export(osmium, themePbf, Path.Combine(work, name));
            var relations = Capture(osmium, "cat", themePbf, "-t", "relation", "-f", "opl");
            File.WriteAllText(Path.Combine(work, $"{name}.relations.opl"), relations);
        }

        var source = new SetSource
        {
            File = Path.GetFileName(pbf),
            Sha256 = Sha256(pbf),
            ReplicationTimestamp = DateTimeOffset.TryParse(
                Capture(osmium, "fileinfo", "-g", "header.option.osmosis_replication_timestamp", pbf).Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var timestamp) ? timestamp : null,
            Tools = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["osmium"] = string.Join("; ", Capture(osmium, "--version").Split('\n').Take(2).Select(l => l.Trim())),
            },
        };
        File.WriteAllText(Path.Combine(work, "source.json"), JsonSerializer.Serialize(source, PipelineParams.JsonOptions));
    }

    private static void Export(string osmium, string input, string outputStem)
    {
        var errors = Capture(
            osmium, "export", input, "-f", "geojsonseq", "-a", "type,id", "-x", "print_record_separator=false", "--show-errors", "-O", "-o", outputStem + ".geojsonseq");
        File.WriteAllText(outputStem + ".errors.txt", errors);
    }

    private static void Osmium(string osmium, params string[] arguments) => Capture(osmium, arguments);

    /// <summary>Запуск osmium; stdout и stderr — вместе (в них журнал ошибок экспорта).</summary>
    private static string Capture(string osmium, params string[] arguments)
    {
        var start = new ProcessStartInfo(osmium) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Не запустился {osmium}.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var output = stdout + stderr.Result;
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"osmium {string.Join(' ', arguments)} — код {process.ExitCode}:\n{output}");
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>Чтение результата <see cref="Extract"/> в <see cref="OsmInput"/> и проверка «все отношения-площади собраны».</summary>
public static class InputLoader
{
    public sealed record Loaded(OsmInput Input, SetSource Source, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

    public static Loaded Load(PipelineParams parameters, string work)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var features = new List<OsmFeature>();
        var seen = new HashSet<(string, long, string)>();
        foreach (var (name, _) in Extract.Themes(parameters))
        {
            var exported = GeoJsonSeq.ReadFile(Path.Combine(work, $"{name}.geojsonseq")).ToList();
            foreach (var feature in exported)
            {
                if (seen.Add((feature.Type, feature.Id, feature.Geometry.GeometryType)))
                {
                    features.Add(feature);
                }
            }

            CheckRelations(name, work, exported, parameters, problems, notes);
        }

        // getid -r тянет и вложенные отношения (области страны) — разбираем только нужные границы.
        var wanted = new HashSet<long>([parameters.CityRelationId, parameters.CountryRelationId, .. parameters.DistrictRelationIds]);
        var borders = GeoJsonSeq.ReadFile(Path.Combine(work, "borders.geojsonseq"), (type, id) => type == "relation" && wanted.Contains(id))
            .Where(f => f.IsArea)
            .ToDictionary(f => f.Id);
        OsmFeature Border(long id) =>
            borders.TryGetValue(id, out var feature) ? feature : throw new InvalidOperationException($"Граница r{id} не собралась в многоугольник.");

        var city = Border(parameters.CityRelationId);
        var input = new OsmInput
        {
            Features = features,
            City = city.Geometry,
            CityName = city.Tag("name:ru") ?? city.Tag("name") ?? "Брест",
            CityOsmId = city.Id,
            BorderLine = GeoOps.LineInside(Border(parameters.CountryRelationId).Geometry.Boundary, FrameRectangle(parameters)),
            Districts = [.. parameters.DistrictRelationIds.Select(Border).Select(d => new DistrictInput(d.Id, d.Tag("name:ru") ?? d.Tag("name") ?? $"r{d.Id}", d.Geometry))],
        };
        var source = JsonSerializer.Deserialize<SetSource>(File.ReadAllText(Path.Combine(work, "source.json")), PipelineParams.JsonOptions) ?? new SetSource();
        source.Tools["NetTopologySuite"] = typeof(NetTopologySuite.Geometries.Geometry).Assembly.GetName().Version?.ToString() ?? "?";
        source.Tools[".NET"] = Environment.Version.ToString();
        return new Loaded(input, source, problems, notes);
    }

    /// <summary>Прямоугольник тайлов рамки в UTM.</summary>
    public static NetTopologySuite.Geometries.Geometry FrameRectangle(PipelineParams parameters)
    {
        var range = TileRange.Of(parameters.Frame);
        return GeoOps.Factory.ToGeometry(new NetTopologySuite.Geometries.Envelope(
            range.MinX * TileKey.SizeMeters, (range.MaxX + 1) * TileKey.SizeMeters, range.MinY * TileKey.SizeMeters, (range.MaxY + 1) * TileKey.SizeMeters));
    }

    /// <summary>
    /// Каждое отношение-площадь темы есть в экспорте многоугольником (osm-pipeline.md, «Инвариант набора»). osmium молча
    /// пропускает несобранный многоугольник — без этой сверки маска реки могла бы пропасть без единой ошибки.
    /// </summary>
    private static void CheckRelations(
        string theme, string work, IReadOnlyList<OsmFeature> exported, PipelineParams parameters, List<string> problems, List<string> notes)
    {
        var areaRelations = File.ReadAllLines(Path.Combine(work, $"{theme}.relations.opl"))
            .Select(ParseOplRelation)
            .Where(r => r.Type is "multipolygon" or "boundary")
            .Select(r => r.Id)
            .ToHashSet();
        var assembled = exported.Where(f => f.Type == "relation" && f.IsArea).Select(f => f.Id).ToHashSet();
        var known = parameters.KnownBrokenRelations.ToDictionary(r => r.Id, r => r.Reason);
        foreach (var id in areaRelations.Except(assembled).Order())
        {
            if (known.TryGetValue(id, out var reason))
            {
                notes.Add($"Тема {theme}: отношение r{id} не собрано — известно: {reason}.");
            }
            else if (theme != "areas")
            {
                // Здания и памятники — только кандидаты для карты-артефакта, в маски и «достижимое» они не идут.
                notes.Add($"Тема {theme}: отношение r{id} не собралось в многоугольник (на маски не влияет).");
            }
            else
            {
                problems.Add($"Тема {theme}: отношение-площадь r{id} не собралось в многоугольник (журнал: {theme}.errors.txt).");
            }
        }
    }

    /// <summary>Строка OPL отношения: <c>r123 v1 … Tkey=value,key2=value2 M…</c> → id и значение тега <c>type</c>.</summary>
    public static (long Id, string? Type) ParseOplRelation(string line)
    {
        var fields = line.Split(' ');
        var id = long.Parse(fields[0].AsSpan(1), CultureInfo.InvariantCulture);
        var tags = fields.FirstOrDefault(f => f.StartsWith('T'))?[1..] ?? "";
        var type = tags.Split(',').Select(t => t.Split('=', 2)).FirstOrDefault(kv => kv.Length == 2 && kv[0] == "type")?[1];
        return (id, type);
    }
}
