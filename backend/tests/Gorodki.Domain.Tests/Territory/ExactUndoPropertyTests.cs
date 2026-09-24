using CsCheck;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Независимый оракул точного отката (I8, аудит BE-01): публичная проекция скрытых захватов равна тому же хранилищу, в
/// котором этих захватов не было, — кусок в кусок, номер строки в номер, состояние целиком, контур до вершины. Хранилище —
/// <see cref="FakeTerritoryStore"/> (как у сервера: разница захвата, строки журнала, визиты по номерам, удаление аккаунта);
/// эталон считается им же, но без скрытых захватов, — без <see cref="VisitReplay"/> и без <see cref="ExactUndo"/>.
/// </summary>
/// <remarks>
/// Истории: звёздчатые петли с шумом GPS вокруг угла четырёх тайлов (границы почти всегда наклонные), потом окно из 1–3
/// скрытых захватов вперемешку с событиями окна — визитами владельцев (автора, жертв, соседей: «владелец пробежал по всем
/// своим кускам», не больше одного визита на владельца) и удалением аккаунтов не-авторов. Единственное допустимое
/// расхождение — задокументированный остаток «визит не лёг»: в настоящем мире визит владельца ничего не изменил на его
/// земле над этим куском (её отняли целиком; или треснула целиком, и визит в осаде ничего не дал), а без захвата он бы
/// лёг. Число случаев задаёт GORODKI_GEO_ITERATIONS (как у остальных property-тестов).
/// </remarks>
public sealed class ExactUndoPropertyTests
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

    /// <summary>Петля: звезда вокруг центра с шумом GPS до ±2 м.</summary>
    public sealed record Loop(int Player, double CenterX, double CenterY, double[] Radii, double Rotation, int NoiseSeed)
    {
        public override string ToString() => $"игрок {Player}, центр ({CenterX:0.#}; {CenterY:0.#}), лучей {Radii.Length}, шум {NoiseSeed}";
    }

    /// <summary>Шаг окна: скрытый захват (<see cref="Loop"/>) или событие — визит (<see cref="Delete"/> = false) или удаление.</summary>
    /// <param name="Order">Порядок в окне.</param>
    /// <param name="Minutes">Время захвата или визита — минуты от начала окна (визит может быть и раньше захвата).</param>
    public sealed record WindowStep(double Order, Loop? Loop, int Player, bool Delete, double Minutes)
    {
        public override string ToString() =>
            Loop is not null ? $"скрытый захват ({Loop}) в +{Minutes:0.#} мин"
            : Delete ? $"удаление игрока {Player}" : $"визит игрока {Player} в {Minutes:+0.#;-0.#} мин";
    }

    public sealed record History(Loop[] Public, double[] Hours, double WindowHours, WindowStep[] Window)
    {
        public override string ToString() =>
            string.Join("; ", Public.Select((loop, i) => $"через {Hours[i]:0.#} ч {loop}")) + $" | окно через {WindowHours:0.#} ч: "
            + string.Join("; ", Window.OrderBy(w => w.Order));
    }

    private static Gen<Loop> LoopGen(double spread) =>
        from player in Gen.Int[0, Players.Length - 1]
        from x in Gen.Double[-spread, spread]
        from y in Gen.Double[-spread, spread]
        from radii in Gen.Double[30, 200].Array[6, 16]
        from rotation in Gen.Double[0, 2 * Math.PI]
        from seed in Gen.Int[0, 1_000_000]
        select new Loop(player, x, y, radii, rotation, seed);

    private static readonly Gen<WindowStep> HiddenGen =
        from order in Gen.Double[0, 1]
        from loop in LoopGen(250)
        from minutes in Gen.Double[0, 20]
        select new WindowStep(order, loop, loop.Player, false, minutes);

    private static readonly Gen<WindowStep> EventGen =
        from order in Gen.Double[0, 1]
        from player in Gen.Int[0, Players.Length - 1]
        from delete in Gen.Int[0, 3]
        from minutes in Gen.Double[-10, 25]
        select new WindowStep(order, null, player, delete == 0, minutes);

    // Публичные петли теснее (±150 м): чаще перекрываются, растут уровни, и скрытым захватам есть что треснуть.
    private static readonly Gen<History> HistoryGen =
        from loops in LoopGen(150).Array[1, 5]
        from hours in Gen.Double[0, 30].Array[5]
        from window in Gen.Double[0.5, 30]
        from hidden in HiddenGen.Array[1, 3]
        from events in EventGen.Array[0, 6]
        select new History(loops, hours, window, [.. hidden, .. events]);

    private static Geometry? ShapeOf(Loop loop)
    {
        var vertices = loop.Radii
            .Select((r, k) =>
            {
                var angle = loop.Rotation + (2 * Math.PI * k / loop.Radii.Length);
                return (loop.CenterX + (r * Math.Cos(angle)), loop.CenterY + (r * Math.Sin(angle)));
            })
            .ToList();
        var random = new Random(loop.NoiseSeed);
        var trail = Trail(vertices)
            .Select(c => new Coordinate(c.X + ((random.NextDouble() - 0.5) * 4), c.Y + ((random.NextDouble() - 0.5) * 4)))
            .ToList();
        var shape = CaptureShapeBuilder.Build(trail, 40, null, ShapeSettings);
        return shape.IsAccepted ? shape.Area : null;
    }

    /// <summary>
    /// История, разыгранная на хранилище: до окна, окно по порядку. Шаги окна отфильтрованы так же для любого мира: не
    /// больше одного визита на владельца, удаляются только не-авторы и один раз.
    /// </summary>
    private sealed class Played
    {
        public required FakeTerritoryStore BeforeWindow { get; init; }

        public required DateTimeOffset WindowStart { get; init; }

        public required List<(WindowStep Step, Geometry? Shape)> Window { get; init; }

        public required FakeTerritoryStore Real { get; init; }

        /// <summary>Скрытые захваты, записанные в журнал, и шаг окна каждого.</summary>
        public required List<(FakeTerritoryStore.Journaled Entry, WindowStep Step)> Hidden { get; init; }

        /// <summary>Какие куски в настоящем мире изменил визит владельца (контуры в момент визита).</summary>
        public required Dictionary<Guid, List<Polygon>> Touched { get; init; }

        /// <summary>Вся земля владельца в настоящем мире в момент его визита.</summary>
        public required Dictionary<Guid, List<Polygon>> Owned { get; init; }
    }

    private static Played Play(History history)
    {
        var store = new FakeTerritoryStore();
        var time = T0;
        for (var i = 0; i < history.Public.Length; i++)
        {
            time = time.AddHours(history.Hours[i]);
            if (ShapeOf(history.Public[i]) is { } shape)
            {
                store.Capture(shape, new CaptureContext(Players[history.Public[i].Player], time, new HashSet<Guid>()));
            }
        }

        var windowStart = time.AddHours(history.WindowHours);
        var authors = history.Window.Where(w => w.Loop is not null).Select(w => w.Player).ToHashSet();
        var visited = new HashSet<int>();
        var deleted = new HashSet<int>();
        var window = new List<(WindowStep Step, Geometry? Shape)>();
        foreach (var step in history.Window.OrderBy(w => w.Order))
        {
            if (step.Loop is { } loop)
            {
                window.Add((step, ShapeOf(loop)));
            }
            else if (step.Delete ? !authors.Contains(step.Player) && deleted.Add(step.Player) : visited.Add(step.Player))
            {
                window.Add((step, null));
            }
        }

        var beforeWindow = store.Clone();
        var hidden = new List<(FakeTerritoryStore.Journaled, WindowStep)>();
        var touched = new Dictionary<Guid, List<Polygon>>();
        var owned = new Dictionary<Guid, List<Polygon>>();
        foreach (var (step, shape) in window)
        {
            var player = Players[step.Player];
            var at = windowStart.AddMinutes(step.Minutes);
            if (step.Loop is not null)
            {
                if (shape is not null && store.Capture(shape, new CaptureContext(player, at, new HashSet<Guid>())) is { Changes.Count: > 0 } entry)
                {
                    hidden.Add((entry, step));
                }
            }
            else if (step.Delete)
            {
                store.Delete(player);
            }
            else
            {
                owned[player] = [.. store.Rows.Where(r => r.Parcel.State.OwnerId == player).Select(r => r.Parcel.Geometry)];
                var ids = store.VisitAll(player, at).ToHashSet();
                touched[player] = [.. store.Rows.Where(r => ids.Contains(r.Id)).Select(r => r.Parcel.Geometry)];
            }
        }

        return new Played { BeforeWindow = beforeWindow, WindowStart = windowStart, Window = window, Real = store, Hidden = hidden, Touched = touched, Owned = owned };
    }

    /// <summary>
    /// Тот же мир, но из скрытых захватов — только выбранные (<paramref name="keep"/>), и, если задан, без визита этого
    /// владельца.
    /// </summary>
    private static FakeTerritoryStore Replay(Played played, Func<WindowStep, bool> keep, Guid? withoutVisitOf = null)
    {
        var store = played.BeforeWindow.Clone();
        foreach (var (step, shape) in played.Window)
        {
            var player = Players[step.Player];
            var at = played.WindowStart.AddMinutes(step.Minutes);
            if (step.Loop is not null)
            {
                if (shape is not null && keep(step))
                {
                    store.Capture(shape, new CaptureContext(player, at, new HashSet<Guid>()));
                }
            }
            else if (step.Delete)
            {
                store.Delete(player);
            }
            else if (player != withoutVisitOf)
            {
                store.VisitAll(player, at);
            }
        }

        return store;
    }

    /// <summary>
    /// Расхождение проекции с эталоном по куску <paramref name="expected"/> — или <c>null</c>, если его нет или это
    /// допустимый остаток «визит не лёг» (<paramref name="missed"/> растёт: [0] — земли владельца над куском не осталось,
    /// [1] — осталась, но визит на ней ничего не изменил).
    /// </summary>
    private static string? Mismatch(
        Played played,
        Func<Guid, FakeTerritoryStore> withoutVisitOf,
        (long Id, Parcel Parcel) expected,
        IReadOnlyList<ProjectedParcel> projection,
        int[] missed)
    {
        var (id, parcel) = expected;
        if (projection.FirstOrDefault(p => p.Id == id) is not { } seen)
        {
            return $"нет строки {id} ({parcel.State.OwnerId}, {parcel.Geometry.Area:0.#} м²)";
        }

        if (!seen.Parcel.Geometry.EqualsExact(parcel.Geometry))
        {
            return $"строка {id}: другой контур {seen.Parcel.Geometry} вместо {parcel.Geometry}";
        }

        if (seen.Parcel.State == parcel.State)
        {
            return null;
        }

        // Остаток «визит не лёг»: без этого визита эталон совпал бы, а в настоящем мире визит владельца не изменил ничего
        // на его земле над этим куском.
        var owner = parcel.State.OwnerId;
        bool Over(IEnumerable<Polygon> land) =>
            land.Any(g => g.EnvelopeInternal.Intersects(parcel.Geometry.EnvelopeInternal)
                && GeoOps.Intersection(g, parcel.Geometry).Area > (0.01 * parcel.Geometry.Area) + 1);
        if (played.Touched.TryGetValue(owner, out var touched) && !Over(touched)
            && withoutVisitOf(owner).Rows.FirstOrDefault(r => r.Id == id).Parcel?.State == seen.Parcel.State)
        {
            missed[Over(played.Owned[owner]) ? 1 : 0]++;
            return null;
        }

        return $"строка {id}: состояние {seen.Parcel.State} вместо {parcel.State}";
    }

    private static List<string> Compare(
        Played played, FakeTerritoryStore expected, Func<WindowStep, bool> keep, IReadOnlyCollection<FakeTerritoryStore.Journaled> hidden,
        Dictionary<UndoPath, int> paths, int[] missed)
    {
        var cache = new Dictionary<Guid, FakeTerritoryStore>();
        FakeTerritoryStore WithoutVisitOf(Guid owner) =>
            cache.TryGetValue(owner, out var store) ? store : cache[owner] = Replay(played, keep, owner);

        var errors = new List<string>();
        var tiles = played.Real.Tiles.Concat(expected.Tiles).Concat(hidden.SelectMany(h => h.Changes.Keys)).Distinct().Order();
        foreach (var tile in tiles)
        {
            var projection = played.Real.Project(tile, hidden);
            foreach (var path in projection.Paths)
            {
                paths[path] = paths.GetValueOrDefault(path) + 1;
            }

            var rows = expected.RowsIn(tile);
            var before = errors.Count;

            // Точно — всё; запасной путь допустим только там, где удаление аккаунта стёрло все строки обмена (у сервера
            // такой тайл не отличить от захвата без строк; возвращать там нечего, итог тот же — его проверяет сравнение ниже).
            var emptied = hidden.Count(h => h.EmptiedByDeletion.Contains(tile));
            if (projection.Paths.Count(p => p != UndoPath.Exact) > emptied
                || projection.Paths.Any(p => p is not (UndoPath.Exact or UndoPath.NoRows)))
            {
                errors.Add($"тайл {tile}: запасной путь");
            }

            if (rows.Count != projection.Pieces.Count)
            {
                errors.Add($"тайл {tile}: кусков {projection.Pieces.Count}, а ждали {rows.Count}");
            }

            foreach (var row in rows)
            {
                if (Mismatch(played, WithoutVisitOf, row, projection.Pieces, missed) is { } error)
                {
                    errors.Add($"тайл {tile}: {error}");
                }
            }

            if (errors.Count > before)
            {
                errors.Add($"тайл {tile}: пути {string.Join(", ", projection.Paths)}; сейчас: "
                    + string.Join("; ", played.Real.RowsIn(tile).Select(r => $"{r.Id} {r.Parcel.State.OwnerId.ToString()[^1]} {r.Parcel.State.LastVisitAt:HH:mm} {r.Parcel.Geometry.Area:0}")));
            }
        }

        return errors;
    }

    [Fact]
    public void Projection_of_hidden_captures_equals_the_store_without_them()
    {
        var totals = new Dictionary<UndoPath, int>();
        var missedVisits = new int[2];
        var histories = 0;
        var rows = 0;
        var gate = new object();

        HistoryGen.Sample(history =>
        {
            var played = Play(history);
            if (played.Hidden.Count == 0)
            {
                return;
            }

            // Зритель, который сам не захватывал: скрыты все захваты окна.
            var paths = new Dictionary<UndoPath, int>();
            var missed = new int[2];
            var expected = Replay(played, _ => false);
            var errors = Compare(played, expected, _ => false, [.. played.Hidden.Select(h => h.Entry)], paths, missed);

            Assert.True(errors.Count == 0, $"{history}\n{string.Join("\n", errors.Take(10))}");
            lock (gate)
            {
                histories++;
                rows += expected.Rows.Count;
                missedVisits[0] += missed[0];
                missedVisits[1] += missed[1];
                foreach (var (path, count) in paths)
                {
                    totals[path] = totals.GetValueOrDefault(path) + count;
                }
            }
        }, iter: Math.Max(20, Iterations / 3));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"I8, зритель без захватов: историй {histories}, откатов тайлов {totals.Values.Sum()}: "
            + string.Join(", ", totals.OrderBy(t => t.Key).Select(t => $"{t.Key} {t.Value}"))
            + $"; кусков сравнено {rows}, из них «визит не лёг» — {missedVisits[0]} (земля отнята целиком) и {missedVisits[1]} (визит ничего не изменил)");
        Assert.True(totals.GetValueOrDefault(UndoPath.Exact) > 0, "ни одного скрытого захвата — генератор историй сломан");
    }

    [Fact]
    public void Hidden_authors_see_their_own_land_and_the_earliest_sees_the_land_right_after_his_capture()
    {
        // Самый ранний автор окна (если других скрытых захватов у него нет): видит ровно мир сразу после своего захвата —
        // все остальные захваты позже его и откатываются точно. Поздние авторы: их захват мог переписать куски, которые
        // вставил более ранний захват, — тогда он откатывается запасным путём; инварианты карты I1/I2/I6 и своя земля
        // целиком на месте. Доля запасного пути — в выводе теста.
        var earliest = new Dictionary<UndoPath, int>();
        var later = new Dictionary<UndoPath, int>();
        var gate = new object();

        HistoryGen.Sample(history =>
        {
            var played = Play(history);
            if (played.Hidden.Count == 0)
            {
                return;
            }

            var first = played.Hidden[0];
            var laterPaths = new Dictionary<UndoPath, int>();
            var earliestPaths = new Dictionary<UndoPath, int>();
            if (played.Hidden.Count(h => h.Entry.Author == first.Entry.Author) == 1)
            {
                var others = played.Hidden.Where(h => h.Entry.Author != first.Entry.Author).Select(h => h.Entry).ToList();
                bool Keep(WindowStep step) => ReferenceEquals(step, first.Step);
                var errors = Compare(played, Replay(played, Keep), Keep, others, earliestPaths, new int[2]);
                Assert.True(errors.Count == 0, $"{history}\nранний автор {first.Entry.Author}:\n{string.Join("\n", errors.Take(10))}");
            }

            foreach (var author in played.Hidden.Select(h => h.Entry.Author).Distinct().Where(a => a != first.Entry.Author))
            {
                var others = played.Hidden.Where(h => h.Entry.Author != author).Select(h => h.Entry).ToList();
                var projected = new List<Parcel>();
                foreach (var tile in played.Real.Tiles.Concat(others.SelectMany(h => h.Changes.Keys)).Distinct())
                {
                    var projection = played.Real.Project(tile, others);
                    projected.AddRange(projection.Pieces.Select(p => p.Parcel));
                    foreach (var path in projection.Paths)
                    {
                        laterPaths[path] = laterPaths.GetValueOrDefault(path) + 1;
                    }
                }

                var map = new TerritoryMap();
                map.Load(projected);
                var invariants = TerritoryInvariants.Check(map);
                Assert.True(invariants.Count == 0, $"{history}\nавтор {author}: {string.Join("; ", invariants)}");
                var own = GeoOps.UnionAll(played.Real.Rows.Where(r => r.Parcel.State.OwnerId == author).Select(r => (Geometry)r.Parcel.Geometry));
                var seen = GeoOps.UnionAll(projected.Where(p => p.State.OwnerId == author).Select(p => (Geometry)p.Geometry));
                var lost = GeoOps.Difference(own, seen).Area;
                Assert.True(lost <= TerritoryMap.SnapTolerance(own), $"{history}\nавтор {author} не видит {lost:0.##} м² своей земли");
            }

            lock (gate)
            {
                foreach (var (path, count) in earliestPaths)
                {
                    earliest[path] = earliest.GetValueOrDefault(path) + count;
                }

                foreach (var (path, count) in laterPaths)
                {
                    later[path] = later.GetValueOrDefault(path) + count;
                }
            }
        }, iter: Math.Max(20, Iterations / 3));

        static string Describe(Dictionary<UndoPath, int> paths) =>
            $"{paths.Values.Sum()} откатов: " + string.Join(", ", paths.OrderBy(t => t.Key).Select(t => $"{t.Key} {t.Value}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"I8, ранний автор — {Describe(earliest)}; поздние авторы — {Describe(later)}");
    }
}
