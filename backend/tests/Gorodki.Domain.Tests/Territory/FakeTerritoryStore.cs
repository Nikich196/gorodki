using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Хранилище земли, как у сервера, но в памяти: строки кусков с номерами, запись захвата (<c>CaptureProcessor.ApplyAsync</c>),
/// визиты (<c>VisitProcessor</c>), удаление аккаунта (<c>AccountDeletion</c>) и публичная проекция
/// (<c>TerritoryReader.ProjectAsync</c>). Нужно тестам точного отката: сравнить проекцию скрытых захватов с тем же
/// хранилищем без них — кусок в кусок и номер строки в номер.
/// </summary>
/// <remarks>
/// Как у сервера: номера строк — от счётчика (identity), захват пишет только разницу тайла (<see cref="ParcelDiff.Compute"/>)
/// и строки точного отката тем же помощником (<see cref="ParcelDiff.Swap"/>, контуры — через TWKB); журнал — только у
/// тайлов, где разница не пуста; визит — к свежему состоянию строки по её номеру, на месте; удаление — строки игрока прочь,
/// его номер — из списков снявших уровень и в кусках, и в журнале, строки точного отката его кусков — прочь.
/// </remarks>
internal sealed class FakeTerritoryStore(TerritoryRules rules)
{
    private readonly List<(long Id, Parcel Parcel)> _rows = [];
    private readonly List<Journaled> _journal = [];
    private long _lastId;
    private int _lastSeq;

    public FakeTerritoryStore()
        : this(new TerritoryRules())
    {
    }

    public TerritoryRules Rules => rules;

    /// <summary>Захват в журнале: порядок применения (<c>applied_seq</c>), автор, записи по тайлам.</summary>
    public sealed class Journaled(int seq, Guid author)
    {
        public int Seq { get; } = seq;

        public Guid Author { get; } = author;

        public Dictionary<TileKey, TileChange> Changes { get; } = [];

        public Dictionary<TileKey, ParcelSwap?> Swaps { get; } = [];
    }

    public IReadOnlyList<(long Id, Parcel Parcel)> Rows => _rows;

    public IEnumerable<TileKey> Tiles => _rows.Select(r => r.Parcel.Tile).Distinct().Order();

    public IReadOnlyList<(long Id, Parcel Parcel)> RowsIn(TileKey tile) => [.. _rows.Where(r => r.Parcel.Tile == tile)];

    /// <summary>Копия: тот же счётчик номеров, те же строки и журнал (записи неизменяемые, списки — свои).</summary>
    public FakeTerritoryStore Clone()
    {
        var copy = new FakeTerritoryStore(rules) { _lastId = _lastId, _lastSeq = _lastSeq };
        copy._rows.AddRange(_rows);
        foreach (var entry in _journal)
        {
            var clone = new Journaled(entry.Seq, entry.Author);
            foreach (var (tile, change) in entry.Changes)
            {
                clone.Changes[tile] = change;
                clone.Swaps[tile] = entry.Swaps[tile];
            }

            copy._journal.Add(clone);
        }

        return copy;
    }

    /// <summary>Кусок, записанный в обход захвата (например, до исправления BE-01 — в любом порядке обхода), — новой строкой.</summary>
    public long Seed(Parcel parcel)
    {
        _rows.Add((++_lastId, parcel));
        return _lastId;
    }

    /// <summary>
    /// Захват, как <c>CaptureProcessor.ApplyAsync</c>: движок на кусках задетых тайлов, запись разницы, журнал и строки
    /// точного отката по тайлам, где разница не пуста. Возвращает запись журнала (без тайлов — захват ничего не записал).
    /// </summary>
    public Journaled Capture(Geometry area, CaptureContext context)
    {
        var tiles = TileKey.Covering(area.EnvelopeInternal);
        var map = new TerritoryMap(rules, new SliverSettings());
        map.Load(_rows.Where(r => tiles.Contains(r.Parcel.Tile)).Select(r => r.Parcel));
        var result = map.Apply(area, context);

        var entry = new Journaled(++_lastSeq, context.CapturerId);
        foreach (var tile in result.ChangedTiles)
        {
            var before = RowsIn(tile);
            var diff = ParcelDiff.Compute(before, map.ParcelsIn(tile));
            if (diff.IsEmpty)
            {
                continue;
            }

            var addedIds = diff.Added.Select(_ => ++_lastId).ToList();
            _rows.RemoveAll(r => diff.Removed.Contains(r.Id));
            _rows.AddRange(diff.Added.Select((parcel, i) => (addedIds[i], parcel)));
            entry.Changes[tile] = result.Changes.Single(c => c.Tile == tile);
            entry.Swaps[tile] = diff.Swap(tile, before, addedIds);
        }

        if (entry.Changes.Count > 0)
        {
            _journal.Add(entry);
        }

        return entry;
    }

    /// <summary>
    /// Визиты, как <c>VisitProcessor</c>: к свежему состоянию строки по её номеру, на месте (строка и контур те же).
    /// Угасшая до нуля земля и визит, который ничего не меняет, строку не трогают. Возвращает номера изменённых строк.
    /// </summary>
    public IReadOnlyList<long> Visit(IEnumerable<long> ids, DateTimeOffset at)
    {
        var changed = new List<long>();
        foreach (var id in ids.ToList())
        {
            var index = _rows.FindIndex(r => r.Id == id);
            if (index < 0)
            {
                continue;
            }

            var (_, parcel) = _rows[index];
            if (CaptureRules.Visit(parcel.State, at, rules) is { } visited && visited != parcel.State)
            {
                _rows[index] = (id, parcel with { State = visited });
                changed.Add(id);
            }
        }

        return changed;
    }

    /// <summary>Визит владельца на все его куски в один момент.</summary>
    public IReadOnlyList<long> VisitAll(Guid owner, DateTimeOffset at) =>
        Visit(_rows.Where(r => r.Parcel.State.OwnerId == owner).Select(r => r.Id), at);

    /// <summary>
    /// Удаление аккаунта, как <c>AccountDeletion</c>: его куски — прочь, его номер — из списков снявших уровень в чужих
    /// кусках и в журнале (земля до/после и строки точного отката); строки точного отката его кусков — прочь; его захваты
    /// уходят вместе с журналом.
    /// </summary>
    public void Delete(Guid user)
    {
        ParcelState Scrub(ParcelState state) =>
            state.LossAttackers.Contains(user) ? state with { LossAttackers = AttackerSet.Of(state.LossAttackers.Ids.Where(id => id != user)) } : state;

        _rows.RemoveAll(r => r.Parcel.State.OwnerId == user);
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i] = (_rows[i].Id, _rows[i].Parcel with { State = Scrub(_rows[i].Parcel.State) });
        }

        _journal.RemoveAll(entry => entry.Author == user);
        foreach (var entry in _journal)
        {
            foreach (var tile in entry.Changes.Keys.ToList())
            {
                var change = entry.Changes[tile];
                entry.Changes[tile] = change with
                {
                    Before = [.. change.Before.Select(p => p with { State = Scrub(p.State) })],
                    After = [.. change.After.Select(p => p with { State = Scrub(p.State) })],
                };

                if (entry.Swaps[tile] is { } swap)
                {
                    static IReadOnlyList<JournalParcel> Rows(IEnumerable<JournalParcel> rows, Guid user, Func<ParcelState, ParcelState> scrub) =>
                        [.. rows.Where(r => r.State.OwnerId != user).Select(r => r with { State = scrub(r.State) })];

                    var rows = swap with { Replaced = Rows(swap.Replaced, user, Scrub), Written = Rows(swap.Written, user, Scrub) };

                    // У сервера у тайла без строк точного отката их просто нет — как у захвата, записанного до них.
                    entry.Swaps[tile] = rows.Replaced.Count + rows.Written.Count == 0 ? null : rows;
                }
            }
        }
    }

    /// <summary>
    /// Тайл, каким его видит зритель, от которого скрыты эти захваты, — как <c>TerritoryReader.ProjectAsync</c>: строки
    /// хранилища с номерами, скрытые захваты с записью в этом тайле — от новых к старым.
    /// </summary>
    public TileProjection Project(TileKey tile, IEnumerable<Journaled> hidden)
    {
        var inJournal = hidden.Where(h => _journal.Contains(h) && h.Changes.ContainsKey(tile)).OrderByDescending(h => h.Seq);
        return ExactUndo.Project(
            tile,
            [.. RowsIn(tile).Select(r => new ProjectedParcel(r.Id, r.Parcel))],
            [.. inJournal.Select(h => new HiddenTileChange(h.Swaps[tile], () => h.Changes[tile]))],
            rules,
            new SliverSettings());
    }
}
