// Замер скорости движка участков для спайка S13 (PLAN.md, §10, этап 1).
//
// 1. Строим «Арену» 3 × 3 км (9 тайлов): 12 игроков, 150 случайных захватов.
// 2. Меряем новые захваты: шаг A (контур из следа) и шаг B (применение к карте) отдельно,
//    на петлях из 500, 1000 и 2000 сырых точек GPS (точка в секунду, бег ~3 м/с, шум ±2 м).
// 3. Печатаем p50 / p95 / максимум и время самого первого («холодного») захвата.
//
// Критерий спайка: p95 захвата ≤ 2 с на Render Free (0,1 CPU). Здесь меряем на полном ядре
// и отдельно под ограничением CPU (см. docs/adr/0003-territory-engine.md).
//
// Запуск: dotnet run -c Release --project backend/tools/Gorodki.GeoBench [число захватов на размер]

using System.Diagnostics;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;

const double originX = 684_000; // угол четырёх тайлов в Бресте (UTM 34N)
const double originY = 5_775_000;

var perSize = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 100;
var random = new Random(2026);
var players = Enumerable.Range(1, 12).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
var settings = new CaptureShapeSettings();
var time = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

// Холодный старт: первый захват в свежем процессе (JIT, загрузка NTS) — как после засыпания Render.
var coldWatch = Stopwatch.StartNew();
var coldMap = new TerritoryMap();
var coldShape = CaptureShapeBuilder.Build(Loop(random, 500, 0, 0), 50, null, settings);
if (coldShape.IsAccepted)
{
    coldMap.Apply(coldShape.Area, new CaptureContext(players[0], time, new HashSet<Guid>()));
}

coldWatch.Stop();

// 1. Арена.
var map = new TerritoryMap();
var built = 0;
while (built < 150)
{
    var trail = Loop(random, random.Next(200, 900), random.NextDouble() * 3000 - 1500, random.NextDouble() * 3000 - 1500);
    var shape = CaptureShapeBuilder.Build(trail, 50, null, settings);
    if (!shape.IsAccepted)
    {
        continue;
    }

    time = time.AddHours(random.NextDouble() * 10);
    map.Apply(shape.Area, new CaptureContext(players[random.Next(players.Length)], time, new HashSet<Guid>()));
    built++;
}

var pieces = map.Parcels.ToList();
Console.WriteLine($"Арена: {pieces.Count} кусков в {map.Tiles.Count()} тайлах, " +
    $"в среднем {pieces.Average(p => p.Geometry.NumPoints):0} вершин, максимум {pieces.Max(p => p.Geometry.NumPoints)}");
Console.WriteLine($"Холодный первый захват (500 точек): {coldWatch.Elapsed.TotalMilliseconds:0} мс");
Console.WriteLine();
Console.WriteLine("| Точек в петле | Захватов | Шаг A p50 / p95 / макс, мс | Шаг B p50 / p95 / макс, мс | A+B p95, мс | Тайлов за захват |");
Console.WriteLine("|---|---|---|---|---|---|");

// 2. Замеры.
foreach (var size in new[] { 500, 1000, 2000 })
{
    var stepA = new List<double>();
    var stepB = new List<double>();
    var total = new List<double>();
    var tiles = new List<int>();
    while (stepA.Count < perSize)
    {
        var trail = Loop(random, size, random.NextDouble() * 2000 - 1000, random.NextDouble() * 2000 - 1000);
        var watch = Stopwatch.StartNew();
        var shape = CaptureShapeBuilder.Build(trail, 50, null, settings);
        var a = watch.Elapsed.TotalMilliseconds;
        if (!shape.IsAccepted)
        {
            continue;
        }

        time = time.AddHours(random.NextDouble() * 10);
        watch.Restart();
        var result = map.Apply(shape.Area, new CaptureContext(players[random.Next(players.Length)], time, new HashSet<Guid>()));
        var b = watch.Elapsed.TotalMilliseconds;

        stepA.Add(a);
        stepB.Add(b);
        total.Add(a + b);
        tiles.Add(result.ChangedTiles.Count);
    }

    Console.WriteLine($"| {size} | {perSize} | {Stats(stepA)} | {Stats(stepB)} | {Percentile(total, 0.95):0} | {tiles.Average():0.0} |");
}

var final = map.Parcels.ToList();
Console.WriteLine();
Console.WriteLine($"После замеров: {final.Count} кусков, в среднем {final.Average(p => p.Geometry.NumPoints):0} вершин; " +
    $"нарушений инвариантов: {TerritoryInvariants.Check(map).Count}");

static string Stats(List<double> values) =>
    $"{Percentile(values, 0.5):0} / {Percentile(values, 0.95):0} / {values.Max():0}";

static double Percentile(List<double> values, double p)
{
    var sorted = values.Order().ToList();
    return sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)];
}

// Петля бегуна: точка раз в секунду, ~3 м/с, по «звезде» вокруг центра, с шумом GPS ±2 м.
static List<Coordinate> Loop(Random random, int points, double centerX, double centerY)
{
    var perimeter = points * 3.0;
    var rays = random.Next(6, 16);
    var baseRadius = perimeter / (2 * Math.PI);
    var radii = Enumerable.Range(0, rays).Select(_ => baseRadius * (0.6 + random.NextDouble() * 0.6)).ToArray();
    var rotation = random.NextDouble() * Math.PI * 2;
    var trail = new List<Coordinate>(points);
    for (var i = 0; i < points; i++)
    {
        var t = (double)i / points * rays;
        var k = (int)Math.Floor(t);
        var f = t - k;
        var r = radii[k % rays] * (1 - f) + radii[(k + 1) % rays] * f;
        var angle = rotation + 2 * Math.PI * i / points;
        trail.Add(new Coordinate(
            originX + centerX + r * Math.Cos(angle) + (random.NextDouble() - 0.5) * 4,
            originY + centerY + r * Math.Sin(angle) + (random.NextDouble() - 0.5) * 4));
    }

    return trail;
}
