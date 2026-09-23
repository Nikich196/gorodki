using System.Threading.Channels;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;

namespace Gorodki.Api.Features.Realtime;

/// <summary>Подсказка приложению: «перезапроси» — сами данные идут только через REST.</summary>
public abstract record RealtimeHint;

/// <summary>Тайлы лиги изменились: всем, кто смотрит лигу (<see cref="UserId"/> = null), или одному игроку.</summary>
public sealed record TilesChangedHint(League League, IReadOnlyList<TileKey> Tiles, Guid? UserId) : RealtimeHint;

/// <summary>Заявка петли решена — только её автору.</summary>
public sealed record CaptureDecidedHint(Guid UserId, Guid RunId, Guid CaptureId, string Status) : RealtimeHint;

/// <summary>
/// Очередь подсказок реального времени (PLAN.md, D6). Обработчики кладут сюда подсказку <b>после</b> фиксации транзакции —
/// подсказка о несуществующем изменении невозможна, — а отправляет отдельная служба (<see cref="RealtimePump"/>): медленное
/// соединение не задерживает поток захватов. Очередь ограничена: при переполнении теряются самые старые подсказки —
/// не страшно, приложение пересинхронизируется по версиям тайлов при каждом подключении и изредка опрашивает само.
/// </summary>
public sealed class RealtimeHints
{
    private readonly Channel<RealtimeHint> _hints = Channel.CreateBounded<RealtimeHint>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public ChannelReader<RealtimeHint> Reader => _hints.Reader;

    public void Publish(RealtimeHint hint) => _hints.Writer.TryWrite(hint);

    /// <summary>Тайлы изменились публично — всем, кто смотрит лигу.</summary>
    public void TilesChanged(League league, IEnumerable<TileKey> tiles) => Publish(new TilesChangedHint(league, [.. tiles], null));

    /// <summary>Тайлы изменились для одного игрока (например, его собственный захват — он его видит сразу).</summary>
    public void TilesChangedFor(Guid userId, League league, IEnumerable<TileKey> tiles) =>
        Publish(new TilesChangedHint(league, [.. tiles], userId));
}
