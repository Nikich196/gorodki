// Конвейер OSM v1 (docs/architecture/osm-pipeline.md; как запускать — docs/guides/osm-pipeline-run.md).
//
// Geofabrik → osmium (вырезка, темы, экспорт) → C#/NTS на сетке 0,1 м (маски, «достижимое», районы, Арена) → проверка
// инвариантов → набор osm-set-N.zip → загрузка в базу. Инструмент офлайн: сервер только читает готовые таблицы.
//
//   extract --pbf belarus-latest.osm.pbf --work <папка> [--params osm-pipeline.json] [--osmium osmium]
//   build   --work <папка> --version N --out osm-set-N.zip [--params …]
//   import  --set osm-set-N.zip                  строка подключения — в переменной GORODKI_OSM_DB
//   preview --set osm-set-N.zip --work <папка> --out карта.html [--params …]

using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.OsmPipeline;
using Microsoft.EntityFrameworkCore;

try
{
    return await Cli.RunAsync(args);
}
catch (UsageException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Cli.Usage);
    return 2;
}

namespace Gorodki.OsmPipeline
{
    /// <summary>Неверные параметры запуска.</summary>
    public sealed class UsageException(string message) : Exception(message);

    public static class Cli
    {
        public const string Usage = """
            Конвейер OSM «Городков» (docs/guides/osm-pipeline-run.md).

              extract --pbf belarus-latest.osm.pbf --work <папка> [--params osm-pipeline.json] [--osmium osmium]
              build   --work <папка> --version N --out osm-set-N.zip [--params osm-pipeline.json]
              import  --set osm-set-N.zip          (строка подключения — в переменной GORODKI_OSM_DB)
              preview --set osm-set-N.zip --work <папка> --out карта.html [--params osm-pipeline.json]

            Параметры по умолчанию — osm-pipeline.json рядом с программой. Данные OSM и наборы — вне репозитория.
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0)
            {
                throw new UsageException("Нет команды.");
            }

            var options = Options(args[1..]);
            string Required(string name) => options.TryGetValue(name, out var value) ? value : throw new UsageException($"Нужен --{name}.");
            PipelineParams Params() =>
                PipelineParams.FromJson(File.ReadAllText(options.GetValueOrDefault("params") ?? Path.Combine(AppContext.BaseDirectory, "osm-pipeline.json")));

            switch (args[0])
            {
                case "extract":
                    Extract.Run(Params(), Required("pbf"), Required("work"), options.GetValueOrDefault("osmium") ?? "osmium");
                    Console.WriteLine($"Темы и границы — в {Required("work")}.");
                    return 0;

                case "build":
                    return Build(Params(), Required("work"), int.Parse(Required("version"), System.Globalization.CultureInfo.InvariantCulture), Required("out"));

                case "import":
                    return await ImportAsync(Required("set"));

                case "preview":
                    var set = OsmSetFile.Read(Required("set"));
                    var loaded = InputLoader.Load(Params(), Required("work"));
                    File.WriteAllText(Required("out"), Preview.Html(set, loaded.Input, Params()));
                    Console.WriteLine($"Карта: {Required("out")}");
                    return 0;

                default:
                    throw new UsageException($"Неизвестная команда: {args[0]}");
            }
        }

        private static int Build(PipelineParams parameters, string work, int version, string output)
        {
            if (version < 1)
            {
                throw new UsageException("Номер набора — от 1.");
            }

            var started = DateTimeOffset.UtcNow;
            var loaded = InputLoader.Load(parameters, work);
            var data = SetBuilder.Build(loaded.Input, parameters, TileRange.Of(parameters.Frame));
            var withNotes = new OsmSetData
            {
                Frame = data.Frame,
                PlayZone = data.PlayZone,
                Masks = data.Masks,
                Land = data.Land,
                Reachable = data.Reachable,
                Districts = data.Districts,
                Notes = [.. loaded.Notes, .. data.Notes],
            };
            var problems = loaded.Problems.Concat(SetVerifier.Verify(withNotes)).ToList();
            Console.WriteLine(Summary(withNotes));
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("Набор не выпущен — нарушены инварианты:");
                foreach (var problem in problems)
                {
                    Console.Error.WriteLine($"  - {problem}");
                }

                return 1;
            }

            OsmSetFile.Write(output, version, withNotes, parameters, loaded.Source, DateTimeOffset.UtcNow);
            var written = OsmSetFile.Read(output);
            Console.WriteLine($"Набор {version}: {output}, отпечаток {written.Fingerprint}, {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} с.");
            return 0;
        }

        private static async Task<int> ImportAsync(string path)
        {
            var connection = Environment.GetEnvironmentVariable(SetImporter.ConnectionVariable);
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new UsageException($"Строка подключения — в переменной окружения {SetImporter.ConnectionVariable} (в аргументах её не передавать).");
            }

            var set = OsmSetFile.Read(path);
            var options = new DbContextOptionsBuilder<AppDbContext>();
            AppDbContext.Configure(options, connection);
            await using var db = new AppDbContext(options.Options);
            var outcome = await SetImporter.ImportAsync(db, set, DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine(outcome == SetImporter.Outcome.Imported
                ? $"Набор {set.Version} загружен ({set.Data.Masks.Count} кусков масок, {set.Data.Reachable.Count} тайлов «достижимого»). Включить — новой версией игрового конфига: osm.setVersion = {set.Version}."
                : $"Набор {set.Version} с тем же отпечатком уже загружен — ничего не изменено.");
            return 0;
        }

        public static string Summary(OsmSetData data)
        {
            var lines = new List<string> { $"Рамка: тайлы {data.Frame.MinX}–{data.Frame.MaxX} × {data.Frame.MinY}–{data.Frame.MaxY}, зона игры: {data.PlayZone}." };
            foreach (var group in data.Masks.GroupBy(m => m.Kind).OrderBy(g => g.Key))
            {
                lines.Add($"  маска {group.Key}: {group.Count()} кусков, {group.Sum(m => m.Geometry.NumPoints)} вершин, {group.Sum(m => m.Geometry.Area) / 10_000:F1} га");
            }

            lines.Add($"  земля ×0,5: {data.Land.Count} кусков, {data.Land.Sum(m => m.Geometry.Area) / 10_000:F1} га");
            lines.Add($"  «достижимое»: {data.Reachable.Count} тайлов z14, {data.ReachableCells} клеток");
            foreach (var district in data.Districts)
            {
                lines.Add($"  {district.Key} «{district.Name}»{(district.Proposal ? " (предложение)" : "")}: {district.Geometry.Area / 1_000_000:F2} км², без масок {district.AreaWithoutMasks / 1_000_000:F2} км², {district.CellCount} клеток");
            }

            lines.AddRange(data.Notes.Select(n => $"  примечание: {n}"));
            return string.Join('\n', lines);
        }

        private static Dictionary<string, string> Options(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new UsageException($"Ожидался «--имя значение»: {args[i]}");
                }

                options[args[i][2..]] = args[++i];
            }

            return options;
        }
    }
}
