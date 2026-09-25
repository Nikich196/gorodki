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
                var piecesBefore = map.Tiles.ToDictionary(tile => tile, tile => map.ParcelsIn(tile));

                // Каждая четвёртая петля — «большая» (§3.3, #48): чужая земля внутри только помечается «спорной». Признак —
                // из шума, а не из генератора: так истории прежних seed не меняются.
                var result = map.Apply(capture, new CaptureContext(capturer, time, new HashSet<Guid>(), BigLoop: step.NoiseSeed % 4 == 0));

                // Snap-rounding сдвигает любую точку не дальше полудиагонали клетки сетки (0,0707 м), поэтому
                // площадь меняется не больше чем на 0,0707 × длину границы. Это доказуемая граница, не «на глаз».
                var snapTolerance = TerritoryMap.SnapTolerance(capture);

                // I1, I2, I6: правильная геометрия, без наложений, без осколков.
                var errors = TerritoryInvariants.Check(map);
                Assert.True(errors.Count == 0, $"{step}: {string.Join("; ", errors)}");

                // I7: нетронутая земля не переписывается (аудит BE-01).
                AssertUntouchedLandKept(step, piecesBefore, result, map);

                // Каждый квадратный метр петли получил решение.
                var decided = result.AreaByOutcome.Values.Sum();
                Assert.True(
                    Math.Abs(decided - capture.Area) <= snapTolerance,
                    $"{step}: решено {decided:0.##} из {capture.Area:0.##} м²");

                // I5: не досталось игроку только то, что под щитом, треснуло, упёрлось в лимит снятия уровней, помечено
                // «спорной» большой петлёй или ушло в осколки.
                var notTaken = GeoOps.Difference(capture, LandOf(map, capturer)).Area;
                var protectedArea = result.Area(PieceOutcome.Shielded) + result.Area(PieceOutcome.Cracked)
                    + result.Area(PieceOutcome.LossLimited) + result.Area(PieceOutcome.Superseded)
                    + result.Area(PieceOutcome.Contested);
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

    /// <summary>
    /// I7: переписаны только тайлы, где изменилось состояние земли, и в каждом из них есть что записать; остальные тайлы —
    /// те же объекты кусков; кусок, чьи земля (множество точек) и состояние не изменились, — прежний объект, то есть в базе
    /// он сохраняет номер. Куски, чья граница сдвинулась на сантиметры (излом от snap-rounding у изменённой земли), здесь
    /// не в счёт: это известный остаток (UntouchedLandTests, docs/architecture/territory-map.md).
    /// </summary>
    private static void AssertUntouchedLandKept(
        Step step, Dictionary<TileKey, IReadOnlyList<Parcel>> piecesBefore, CaptureResult result, TerritoryMap map)
    {
        Assert.Equal(result.Changes.Select(c => c.Tile), result.ChangedTiles);
        foreach (var tile in piecesBefore.Keys.Union(result.ChangedTiles))
        {
            var before = piecesBefore.GetValueOrDefault(tile) ?? [];
            var after = map.ParcelsIn(tile);
            if (!result.ChangedTiles.Contains(tile))
            {
                Assert.True(before.SequenceEqual(after, ReferenceEqualityComparer.Instance), $"{step}: тайл {tile} переписан, хотя его нет в списке");
                continue;
            }

            var diff = ParcelDiff.Compute([.. before.Select((piece, i) => ((long)i, piece))], after);
            Assert.False(diff.IsEmpty, $"{step}: тайл {tile} в списке переписанных, но записывать в нём нечего");
            foreach (var piece in after)
            {
                var same = before.FirstOrDefault(old => old.State == piece.State
                    && old.Geometry.EnvelopeInternal.Equals(piece.Geometry.EnvelopeInternal)
                    && old.Geometry.EqualsTopologically(piece.Geometry));
                Assert.True(same is null || ReferenceEquals(same, piece), $"{step}: в тайле {tile} переписан кусок той же земли и того же состояния");
            }
        }
    }

    /// <summary>
    /// Вторая сборка тайла (по линиям журнала) изредка расходится с первой — тогда записываются куски первой, и нетронутую
    /// землю режет граница петли (аудит BE-01). Движок при этом не ошибается, но сервер должен об этом узнать: иначе
    /// частоту в проде не выяснить. История найдена перебором случайных историй этого же генератора (2 случая
    /// на 4 420 захватов). Если здесь вторая сборка перестанет расходиться — это не поломка: нужен другой такой случай.
    /// </summary>
    [Fact]
    public void Reassembly_fallback_is_reported()
    {
        Step[] history =
        [
            new(
                Player: 2,
                CenterX: -96.7420453656195,
                CenterY: 2.1202759827115756,
                Radii:
                [
                    80.04680987915341, 141.80740227541298, 45.78099574697251, 31.463265307929024, 247.6054087456341,
                    174.18177989506245, 115.97574375848087, 49.4069132764856, 80.92696492137712, 68.57144433007177,
                    143.63101844379258, 242.35342845895954, 57.43467903110882,
                ],
                Rotation: 1.626451275747115,
                HoursLater: 1.4616642712902577,
                NoiseSeed: 20331),
            new(
                Player: 1,
                CenterX: -118.48170297149647,
                CenterY: 11.738247616094611,
                Radii:
                [
                    202.965252982902, 247.94762439930236, 234.31064102068106, 189.71358878524674, 196.3405037328324,
                    174.6780194643317, 79.68277292776982, 234.11245060344808, 205.95435586569567, 144.19816544940608,
                    131.48215528646585, 84.42421512418623, 39.82672411474712,
                ],
                Rotation: 6.123787865642118,
                HoursLater: 25.871907154038507,
                NoiseSeed: 201494),
            new(
                Player: 3,
                CenterX: -100.32149353079569,
                CenterY: -128.4464093523316,
                Radii:
                [
                    79.02998006391803, 59.72228585263821, 37.27352942678776, 107.4860905564791, 40.318387341833855,
                    138.46132011546814, 194.7767637878548, 180.4453748047563, 97.47643220586536, 156.69854436381652,
                    177.9261101632966, 67.55191620325293, 220.1679194300286, 57.31654965659443,
                ],
                Rotation: 3.8281322365473818,
                HoursLater: 7.104434383616054,
                NoiseSeed: 579530),
        ];

        var map = new TerritoryMap();
        var time = T0;
        var fallbacks = new List<int>();
        foreach (var step in history)
        {
            time = time.AddHours(step.HoursLater);
            var shape = CaptureShapeBuilder.Build(TrailOf(step), 40, null, ShapeSettings);
            Assert.True(shape.IsAccepted, $"{step}: {shape.Rejection}");
            fallbacks.Add(map.Apply(shape.Area, new CaptureContext(Players[step.Player], time, new HashSet<Guid>())).ReassemblyFallbacks);
            Assert.Empty(TerritoryInvariants.Check(map));
        }

        Assert.Equal([0, 0, 1], fallbacks);
    }

    /// <summary>
    /// Issue #113 (seed clJtO7MV94Z4, история сокращена до одного захвата): петля в 56 369 м² целиком в одном тайле, а
    /// проверка узости сказала, что она уже 1,5 м (<see cref="GeoOps.IsNarrowerThan"/>). Движок сделал весь кусок ничьим
    /// осколком без соседей, в тайле ничего не изменилось, и захват пропал, хотя решения по нему посчитаны.
    /// </summary>
    [Fact]
    public void Wide_capture_is_not_lost_as_a_sliver()
    {
        var step = new Step(
            Player: 0,
            CenterX: 221.9669932274494,
            CenterY: -314.9663212021858,
            Radii:
            [
                182.5032486566879, 190.48866389598618, 123.94527000506139, 206.5466206520099, 138.83699039273705, 212, 34,
                44.61904761904762,
            ],
            Rotation: 0,
            HoursLater: 0,
            NoiseSeed: 820720);
        var shape = CaptureShapeBuilder.Build(TrailOf(step), 40, null, ShapeSettings);
        Assert.True(shape.IsAccepted, $"{step}: {shape.Rejection}");

        var map = new TerritoryMap();
        var result = map.Apply(shape.Area, new CaptureContext(Players[0], T0, new HashSet<Guid>()));

        Assert.Empty(TerritoryInvariants.Check(map));
        Assert.Equal(shape.Area.Area, map.AreaOf(Players[0]), TerritoryMap.SnapTolerance(shape.Area));
        Assert.Equal(0, result.SliverArea);
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
