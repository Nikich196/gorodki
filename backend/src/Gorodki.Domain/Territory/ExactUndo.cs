using Gorodki.Domain.Geo;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Territory;

/// <summary>Строка хранилища, которую захват удалил или вставил в тайле (журнал точного отката).</summary>
/// <param name="ParcelId">Номер строки куска в хранилище (<c>parcels.id</c>).</param>
/// <param name="State">Состояние куска в момент захвата.</param>
/// <param name="Geometry">
/// Контур в TWKB — только у удалённого куска (его строки в хранилище больше нет); у вставленного контур лежит в хранилище.
/// </param>
public sealed record JournalParcel(long ParcelId, ParcelState State, byte[]? Geometry);

/// <summary>
/// Какие строки хранилища захват заменил в тайле: удалённые (<see cref="Replaced"/>) и вставленные вместо них
/// (<see cref="Written"/>). Строится по разнице тайла (<see cref="ParcelDiff.Swap"/>), нужна публичной проекции, чтобы
/// вернуть землю до захвата до вершины и с теми же номерами строк (<see cref="ExactUndo"/>).
/// </summary>
public sealed record ParcelSwap(TileKey Tile, IReadOnlyList<JournalParcel> Replaced, IReadOnlyList<JournalParcel> Written)
{
    /// <summary>
    /// Контур в TWKB, если он читается обратно тем же до вершины и порядка обхода; иначе <c>null</c>. Сетку 0,1 м
    /// <see cref="GeoOps.IsOnGrid(Geometry)"/> проверяет с допуском 1e-6 м, а TWKB читает ровно k/10 — у вершины «почти на
    /// сетке» (кусок, записанный до сетки) контур вернулся бы другим, и проекция выдала бы скрытый захват новым номером.
    /// </summary>
    public static byte[]? Encode(Polygon geometry)
    {
        byte[] bytes;
        try
        {
            bytes = Twkb.Write(geometry);
        }
        catch (ArgumentException)
        {
            return null; // вершины не на сетке
        }

        return Twkb.Read(bytes).EqualsExact(geometry) ? bytes : null;
    }
}

/// <summary>Кусок в проекции и номер его строки в хранилище (<c>null</c> — кусок собран откатом по граням, строки нет).</summary>
public sealed record ProjectedParcel(long? Id, Parcel Parcel);

/// <summary>Каким путём проекция откатила скрытый захват в тайле.</summary>
public enum UndoPath
{
    /// <summary>Точно: земля до захвата до вершины и с теми же номерами строк.</summary>
    Exact,

    /// <summary>Запасной путь: строк точного отката нет (захват записан до них, контур не сохранился без потерь).</summary>
    NoRows,

    /// <summary>Запасной путь: вставленного захватом куска уже нет по номеру — его переписали (свой захват зрителя, откат).</summary>
    Missing,

    /// <summary>Запасной путь: вставленный кусок менялся не только визитами владельца.</summary>
    Changed,

    /// <summary>Запасной путь: прежний кусок налёг бы на оставшийся (землю, которую захват освободил, уже заняли).</summary>
    Overlap,

    /// <summary>Запасной путь: строки не прочитались (испорчены) или расчёт упал.</summary>
    Exception,
}

/// <summary>
/// Скрытый захват в тайле: строки точного отката (<c>null</c> — их нет) и запись журнала для запасного пути. Запись
/// журнала читается, только если запасной путь нужен: испорченная, она не мешает точному откату.
/// </summary>
public sealed record HiddenTileChange(ParcelSwap? Swap, Func<TileChange> Change);

/// <summary>Тайл, каким его видит зритель.</summary>
/// <param name="Pieces">Куски с номерами строк.</param>
/// <param name="Paths">Как откачен каждый скрытый захват — от новых к старым.</param>
/// <param name="Errors">Ошибки точного пути (<see cref="UndoPath.Exception"/>) — для журнала сервера.</param>
public sealed record TileProjection(IReadOnlyList<ProjectedParcel> Pieces, IReadOnlyList<UndoPath> Paths, IReadOnlyList<Exception> Errors);

/// <summary>
/// Точный откат скрытого захвата в публичной проекции (PLAN.md, §3.16; аудит BE-01; docs/architecture/territory-map.md).
/// </summary>
/// <remarks>
/// <para>
/// Откат по граням следа (<see cref="TerritoryMap.Restore"/>) собирает землю до захвата заново — с изломами snap-rounding
/// там, где петля пересекла наклонную границу, и с новыми номерами у соседей и у срезанных кусков; визит, засчитанный на
/// остатке куска вне следа, с возвращённой землёй не сливается (шов по линии петли). Точный откат не собирает ничего: он
/// убирает строки, которые захват вставил, и возвращает строки, которые он удалил, — те же до вершины, с теми же номерами.
/// Это точно, потому что контур строки куска после вставки не меняется: на месте её меняют только визиты
/// (<c>VisitProcessor</c>: уровень и два времени) и удаление аккаунта (список снявших уровень) — это инвариант хранилища
/// (docs/architecture/data-model.md), его держит архитектурный тест.
/// </para>
/// <para>
/// Визиты владельцев, засчитанные, пока захват скрыт, переносятся: время каждого визита на вставленный кусок
/// (<see cref="VisitReplay.Trace"/>) ложится на удалённый кусок того же владельца, над которым он лежит, — заново от его
/// состояния (<see cref="VisitReplay.Apply"/>). Так жертва, пробежавшая по треснувшей части и по остатку, получает назад
/// один кусок с визитом, а не два со швом. Чего не объяснить визитами — запасной путь.
/// </para>
/// </remarks>
public static class ExactUndo
{
    /// <summary>
    /// Откатывает скрытые захваты тайла от новых к старым: каждый — точно, если можно, иначе по граням следа
    /// (<see cref="Restore"/>). Номера строк переходят к следующему (более старому) захвату: так откатываются и захваты
    /// друг на друге — вставленное старым захватом новый удалил, и точный откат нового вернул его с тем же номером.
    /// </summary>
    /// <exception cref="TerritoryEngineException">Запасной путь не сошёлся.</exception>
    /// <exception cref="FormatException">Запись журнала для запасного пути испорчена.</exception>
    public static TileProjection Project(
        TileKey tile,
        IReadOnlyList<ProjectedParcel> stored,
        IEnumerable<HiddenTileChange> newestFirst,
        TerritoryRules rules,
        SliverSettings slivers)
    {
        var pieces = stored;
        var paths = new List<UndoPath>();
        var errors = new List<Exception>();
        foreach (var hidden in newestFirst)
        {
            var (exact, path, error) = hidden.Swap is { } swap ? TryUndo(pieces, swap, rules) : (null, UndoPath.NoRows, null);
            pieces = exact ?? Restore(tile, pieces, hidden.Change(), rules, slivers);
            paths.Add(path);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        return new TileProjection(pieces, paths, errors);
    }

    /// <summary>
    /// Точный откат одного захвата: куски тайла без его вставленных строк, но с удалёнными — или <c>null</c> и причина,
    /// почему нельзя. Никогда не бросает (кроме отмены): любая ошибка — это <see cref="UndoPath.Exception"/> и запасной путь,
    /// а не 500 каждому, кто смотрит тайл.
    /// </summary>
    public static (IReadOnlyList<ProjectedParcel>? Pieces, UndoPath Path, Exception? Error) TryUndo(
        IReadOnlyList<ProjectedParcel> current, ParcelSwap swap, TerritoryRules rules)
    {
        try
        {
            var (pieces, path) = Undo(current, swap, rules);
            return (pieces, path, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, UndoPath.Exception, e);
        }
    }

    private static (IReadOnlyList<ProjectedParcel>? Pieces, UndoPath Path) Undo(
        IReadOnlyList<ProjectedParcel> current, ParcelSwap swap, TerritoryRules rules)
    {
        // 1. Каждый вставленный захватом кусок на месте (по номеру: контур строки не меняется) и с тех пор менялся только
        // визитами владельца. Иначе землю после захвата трогали (свой захват зрителя, откат) — точно вернуть нельзя.
        var written = new List<(Parcel Piece, IReadOnlyList<DateTimeOffset> Visits)>();
        var writtenIds = new HashSet<long>();
        foreach (var row in swap.Written)
        {
            if (current.FirstOrDefault(c => c.Id == row.ParcelId)?.Parcel is not { } piece)
            {
                return (null, UndoPath.Missing);
            }

            if (VisitReplay.Trace(row.State, piece.State, rules) is not { } visits)
            {
                return (null, UndoPath.Changed);
            }

            written.Add((piece, visits));
            writtenIds.Add(row.ParcelId);
        }

        var remaining = current.Where(c => c.Id is not { } id || !writtenIds.Contains(id)).ToList();
        var restored = new List<ProjectedParcel>();
        foreach (var row in swap.Replaced)
        {
            // 2. Удалённый кусок — как он лежал в хранилище: до вершины и порядка обхода (номер куска у зрителя считается
            // от контура), без «причёсывания».
            var geometry = Twkb.Read(row.Geometry ?? throw new FormatException($"У удалённой строки {row.ParcelId} нет контура."))
                as Polygon ?? throw new FormatException($"Контур удалённой строки {row.ParcelId} — не многоугольник.");

            // 3. Визиты его владельца на вставленные поверх него куски — заново от прежнего состояния, как легли бы без
            // захвата. Визиты другого владельца (автор пробежал по взятому) — нет: без захвата эта земля не его.
            var visits = written
                .Where(w => w.Piece.State.OwnerId == row.State.OwnerId && Overlaps(w.Piece.Geometry, geometry))
                .SelectMany(w => w.Visits)
                .Distinct()
                .ToList();

            // 4. Прежний кусок не налезает на оставшиеся: если налезает, землю, которую захват освободил, кто-то уже занял.
            if (remaining.Any(r => r.Parcel.Geometry.EnvelopeInternal.Intersects(geometry.EnvelopeInternal)
                    && GeoOps.Intersection(r.Parcel.Geometry, geometry).Area > TerritoryInvariants.OverlapToleranceSquareMeters))
            {
                return (null, UndoPath.Overlap);
            }

            restored.Add(new ProjectedParcel(row.ParcelId, new Parcel(swap.Tile, geometry, VisitReplay.Apply(row.State, visits, rules))));
        }

        return ([.. remaining, .. restored], UndoPath.Exact);
    }

    /// <summary>
    /// Вставленный кусок лежит над удалённым: внутренняя точка одного — внутри другого, а если нет — их пересечение где-то
    /// шире 20 см. Не по рамкам: у соседних кусков рамки пересекаются. И не по площади пересечения: излом snap-rounding на
    /// общей наклонной границе даёт соседям ненулевую площадь (до 0,07 м × длину границы), но полоской не шире 7 см.
    /// Внутренних точек мало, когда вставленный кусок слил остатки двух удалённых кусков одного владельца с одним
    /// состоянием: его точка — в одном из них, а визит на нём ложится на оба (найдено оракулом I8).
    /// </summary>
    private static bool Overlaps(Polygon written, Polygon replaced) =>
        Contains(replaced, GeoOps.InteriorPoint(written))
        || Contains(written, GeoOps.InteriorPoint(replaced))
        || (written.EnvelopeInternal.Intersects(replaced.EnvelopeInternal)
            && GeoOps.Intersection(written, replaced) is { IsEmpty: false } common
            && !GeoOps.IsNarrowerThan(common, KinkWidth));

    /// <summary>Половина ширины, м, уже которой пересечение — излом snap-rounding (до 0,0707 м), а не общая земля.</summary>
    private const double KinkWidth = 0.1;

    private static bool Contains(Polygon area, Coordinate point) =>
        area.EnvelopeInternal.Contains(point) && new IndexedPointInAreaLocator(area).Locate(point) == Location.Interior;

    /// <summary>
    /// Запасной путь — откат по граням следа с переносом визитов (<see cref="TerritoryMap.Restore"/> с <c>replayVisits</c>).
    /// Номер строки остаётся у куска, который откат оставил прежним объектом: так следующий (более старый) захват ещё
    /// может откатиться точно, если его куски откат не задел.
    /// </summary>
    /// <exception cref="TerritoryEngineException">Самопроверка отката не сошлась.</exception>
    public static IReadOnlyList<ProjectedParcel> Restore(
        TileKey tile, IReadOnlyList<ProjectedParcel> current, TileChange change, TerritoryRules rules, SliverSettings slivers)
    {
        var map = new TerritoryMap(rules, slivers);
        map.Load(current.Select(c => c.Parcel));
        map.Restore([change], replayVisits: true);
        return [.. map.ParcelsIn(tile).Select(p => new ProjectedParcel(current.FirstOrDefault(c => ReferenceEquals(c.Parcel, p))?.Id, p))];
    }
}
