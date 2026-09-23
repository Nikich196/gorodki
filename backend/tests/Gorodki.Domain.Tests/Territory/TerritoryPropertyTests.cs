using CsCheck;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Property-тесты движка участков (спайк S13, PLAN.md §10): случайные истории захватов,
/// после каждого — проверка инвариантов. Число случаев задаёт переменная окружения
/// GORODKI_GEO_ITERATIONS (в CI — 150, для вердикта спайка — 10 000).
/// При падении CsCheck печатает seed: его можно вставить в тест и воспроизвести случай.
/// </summary>
public sealed class TerritoryPropertyTests
{
    private static readonly Guid[] Players =
    [
        new("00000000-0000-0000-0000-000000000001"),
        new("00000000-0000-0000-0000-000000000002"),
        new("00000000-0000-0000-0000-000000000003"),
        new("00000000-0000-0000-0000-000000000004"),
    ];

    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly CaptureShapeSettings ShapeSettings = new();

    private static long Iterations =>
        long.TryParse(Environment.GetEnvironmentVariable("GORODKI_GEO_ITERATIONS"), out var n) ? n : 150;

    /// <summary>Один захват: звёздчатая петля вокруг центра с шумом GPS.</summary>
    public sealed record Step(int Player, double CenterX, double CenterY, double[] Radii, double Rotation, double HoursLater, int NoiseSeed)
    {
        public override string ToString() =>
            $"игрок {Player}, центр ({CenterX:0.#}; {CenterY:0.#}), лучей {Radii.Length}, через {HoursLater:0.#} ч, шум {NoiseSeed}";
    }

    // Центры в квадрате ±400 м вокруг угла четырёх тайлов — петли часто пересекают края и углы тайлов.
    private static readonly Gen<Step> StepGen =
        from player in Gen.Int[0, Players.Length - 1]
        from x in Gen.Double[-400, 400]
        from y in Gen.Double[-400, 400]
        from radii in Gen.Double[30, 250].Array[6, 24]
        from rotation in Gen.Double[0, 2 * Math.PI]
        from hours in Gen.Double[0, 30]
        from seed in Gen.Int[0, 1_000_000]
        select new Step(player, x, y, radii, rotation, hours, seed);

    private static readonly Gen<Step[]> HistoryGen = StepGen.Array[1, 8];

    /// <summary>След вокруг звезды: вершины на лучах, точки каждые 5 м, шум GPS до ±2 м.</summary>
    private static List<Coordinate> TrailOf(Step step)
    {
        var vertices = step.Radii
            .Select((r, k) =>
            {
                var angle = step.Rotation + 2 * Math.PI * k / step.Radii.Length;
                return (step.CenterX + r * Math.Cos(angle), step.CenterY + r * Math.Sin(angle));
            })
            .ToList();
        var random = new Random(step.NoiseSeed);
        return Trail(vertices)
            .Select(c => new Coordinate(c.X + (random.NextDouble() - 0.5) * 4, c.Y + (random.NextDouble() - 0.5) * 4))
            .ToList();
    }

    private static Geometry LandOf(TerritoryMap map, Guid player) =>
        GeoOps.UnionAll(map.Parcels.Where(p => p.State.OwnerId == player).Select(p => (Geometry)p.Geometry));

    [Fact]
    public void Invariants_hold_for_random_capture_histories()
    {
        HistoryGen.Sample(history =>
        {
            var map = new TerritoryMap();
            var time = T0;
            foreach (var step in history)
            {
                time = time.AddHours(step.HoursLater);
                var shape = CaptureShapeBuilder.Build(TrailOf(step), 40, null, ShapeSettings);
                if (!shape.IsAccepted)
                {
                    continue;
                }

                var capturer = Players[step.Player];
                var capture = shape.Area;
                var othersOutsideBefore = Players
                    .Where(p => p != capturer)
                    .ToDictionary(p => p, p => GeoOps.Difference(LandOf(map, p), capture).Area);

                var result = map.Apply(capture, new CaptureContext(capturer, time, new HashSet<Guid>()));

                // Snap-rounding сдвигает любую точку не дальше полудиагонали клетки сетки (0,0707 м), поэтому
                // площадь меняется не больше чем на 0,0707 × длину границы. Это доказуемая граница, не «на глаз».
                var snapTolerance = TerritoryMap.SnapTolerance(capture);

                // I1, I2, I6: правильная геометрия, без наложений, без осколков.
                var errors = TerritoryInvariants.Check(map);
                Assert.True(errors.Count == 0, $"{step}: {string.Join("; ", errors)}");

                // Каждый квадратный метр петли получил решение.
                var decided = result.AreaByOutcome.Values.Sum();
                Assert.True(
                    Math.Abs(decided - capture.Area) <= snapTolerance,
                    $"{step}: решено {decided:0.##} из {capture.Area:0.##} м²");

                // I5: не досталось игроку только то, что под щитом, треснуло, упёрлось в лимит снятия уровней
                // или ушло в осколки.
                var notTaken = GeoOps.Difference(capture, LandOf(map, capturer)).Area;
                var protectedArea = result.Area(PieceOutcome.Shielded) + result.Area(PieceOutcome.Cracked)
                    + result.Area(PieceOutcome.LossLimited) + result.Area(PieceOutcome.Superseded);
                Assert.True(
                    Math.Abs(notTaken - protectedArea) <= result.SliverArea + snapTolerance,
                    $"{step}: не взято {notTaken:0.##}, защищено {protectedArea:0.##}, осколки {result.SliverArea:0.##} м²");

                // I4: чужая земля вне петли не меняется (кроме поглощённых осколков).
                foreach (var (player, before) in othersOutsideBefore)
                {
                    var after = GeoOps.Difference(LandOf(map, player), capture).Area;
                    Assert.True(
                        Math.Abs(after - before) <= result.SliverArea + snapTolerance,
                        $"{step}: земля игрока вне петли изменилась с {before:0.##} на {after:0.##} м²");
                }
            }
        }, iter: Iterations);
    }

    [Fact]
    public void Same_history_always_gives_the_same_map()
    {
        HistoryGen.Sample(history =>
        {
            Assert.Equal(Play(history, new TerritoryMap()).StateHash(), Play(history, new TerritoryMap()).StateHash());
        }, iter: Math.Max(20, Iterations / 5));
    }

    /// <summary>
    /// Растровый оракул: в режиме «последний захват побеждает» (без щитов и уровней) владелец любой точки —
    /// автор последней петли, которая её накрыла. Сравниваем площади игроков с подсчётом по пикселям 2 × 2 м.
    /// </summary>
    [Fact]
    public void Areas_match_a_raster_oracle_when_the_last_capture_wins()
    {
        const double pixel = 2.0;
        var rules = new TerritoryRules { MaxLevel = 1, TransferShield = TimeSpan.Zero };

        HistoryGen.Sample(history =>
        {
            var map = new TerritoryMap(rules, new SliverSettings());
            var owners = new Dictionary<(int, int), int>();
            var boundaryLength = 0.0;
            var sliverArea = 0.0;
            var time = T0;

            foreach (var step in history)
            {
                time = time.AddHours(step.HoursLater);
                var shape = CaptureShapeBuilder.Build(TrailOf(step), 40, null, ShapeSettings);
                if (!shape.IsAccepted)
                {
                    continue;
                }

                sliverArea += map.Apply(shape.Area, new CaptureContext(Players[step.Player], time, new HashSet<Guid>())).SliverArea;
                boundaryLength += shape.Area.Boundary.Length;

                // Пиксели, центры которых внутри петли, переходят к её автору.
                var locator = new IndexedPointInAreaLocator(shape.Area);
                var envelope = shape.Area.EnvelopeInternal;
                for (var i = (int)Math.Floor(envelope.MinX / pixel); i <= (int)Math.Floor(envelope.MaxX / pixel); i++)
                {
                    for (var j = (int)Math.Floor(envelope.MinY / pixel); j <= (int)Math.Floor(envelope.MaxY / pixel); j++)
                    {
                        var center = new Coordinate((i + 0.5) * pixel, (j + 0.5) * pixel);
                        if (locator.Locate(center) == Location.Interior)
                        {
                            owners[(i, j)] = step.Player;
                        }
                    }
                }
            }

            // Ошибка растра — не больше «полоски» в пиксель вдоль всех границ.
            var tolerance = boundaryLength * pixel + sliverArea + 10;
            for (var player = 0; player < Players.Length; player++)
            {
                var raster = owners.Values.Count(o => o == player) * pixel * pixel;
                var vector = map.AreaOf(Players[player]);
                Assert.True(
                    Math.Abs(raster - vector) <= tolerance,
                    $"игрок {player}: вектор {vector:0.#} м², растр {raster:0.#} м², допуск {tolerance:0.#}");
            }
        }, iter: Math.Max(20, Iterations / 5));
    }

    private static TerritoryMap Play(Step[] history, TerritoryMap map)
    {
        var time = T0;
        foreach (var step in history)
        {
            time = time.AddHours(step.HoursLater);
            var shape = CaptureShapeBuilder.Build(TrailOf(step), 40, null, ShapeSettings);
            if (shape.IsAccepted)
            {
                map.Apply(shape.Area, new CaptureContext(Players[step.Player], time, new HashSet<Guid>()));
            }
        }

        return map;
    }
}
