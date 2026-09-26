import GameCore
import Networking
import Sync

/// Туман игрока за всё время для «≈+N га» на HUD (docs/architecture/run-hud.md, «+N га тумана») — поверх `FogCache`
/// слоя «Пешком»: трекер просит каждый новый тайл забега один раз, кэш берёт его с диска или с сервера. Нет сети или
/// запрос не удался — `nil`: тайл неизвестен, на HUD «≥». Вело — свой слой с Сезона 1: пока неизвестен.
struct CachedAllTimeFog: AllTimeFogProvider {
    let cache: FogCache

    func allTimeTile(_ key: FogTileKey, league: GameCore.League) async -> FogTileBits? {
        guard league == .run else { return nil }
        let ref = FogTileRef(x: key.x, y: key.y)
        _ = try? await cache.refresh(visible: [ref])
        guard let tile = await cache.tile(ref) else { return nil }
        return FogTileBits(words: tile.words)
    }
}
