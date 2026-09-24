import GorodkiAPI

/// Лига — как в API: `run` или `bike`.
public typealias League = Components.Schemas.League

/// Тайл карты — клетка UTM 1×1 км, как в `GET /territory` (docs/architecture/territory-map.md).
public struct TileKey: Hashable, Sendable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }
}

/// Подсказка реального времени (docs/architecture/realtime.md, «Контракт для приложения»). Данных в ней нет — только
/// «что перезапросить» через REST.
public enum RealtimeEvent: Equatable, Sendable {
    /// Соединение установлено и лига выбрана. Подсказки за время разрыва потеряны: дослать очередь, перезапросить
    /// видимые тайлы и итоги ожидающих заявок.
    case connected
    /// `TilesChanged`: в этих тайлах лиги что-то изменилось — перезапросить видимые из них с известными версиями.
    case tilesChanged(League, [TileKey])
    /// `CaptureDecided`: заявка решена — забрать итог (`GET /runs/{runId}/captures`).
    case captureDecided(runId: String, captureId: String, status: String)
}

/// Одно соединение с хабом. Подсказки идут в `messages`, поток кончается, когда соединение закрылось.
public protocol RealtimeConnection: Sendable {
    var messages: AsyncStream<RealtimeEvent> { get }
    /// `Subscribe`: слушать эту лигу (прежняя снимается).
    func subscribe(to league: League) async throws
    func close() async
}

/// Как подключиться к хабу. В приложении — SignalR (`SignalRConnector`), в проверках — подмена.
public protocol RealtimeConnector: Sendable {
    /// Новое соединение.
    /// - Throws: `RealtimeConnectError.notSignedIn`, если входа нет, или ошибку сети и сервера.
    func connect() async throws -> any RealtimeConnection
}

public enum RealtimeConnectError: Error, Equatable {
    /// Вход не выполнен или потерян — подключаться не с чем.
    case notSignedIn
}
