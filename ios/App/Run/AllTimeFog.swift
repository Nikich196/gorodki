import GameCore
import Networking
import Sync

/// Туман игрока за всё время для «≈+N га» на HUD (docs/architecture/run-hud.md, «+N га тумана») — поверх `FogCache`
/// слоя «Пешком»: трекер просит каждый новый тайл забега один раз, кэш берёт его с сервера или с диска. Нет сети —
/// тайл с диска, если он есть (он мог устареть: клетки, открытые с тех пор, посчитаются новыми — это «≈»); нет и его —
/// `nil`: тайл неизвестен, на HUD «≥». «Вело» — свой слой «Вело» (`bikeCache`).
struct CachedAllTimeFog: AllTimeFogProvider {
    let cache: FogCache
    var bikeCache: FogCache?

    func allTimeTile(_ key: FogTileKey, league: GameCore.League) async -> FogTileBits? {
        guard let cache = league == .bike ? bikeCache : cache else { return nil }
        let ref = FogTileRef(x: key.x, y: key.y)
        _ = try? await cache.refresh(visible: [ref])
        guard let tile = await cache.tile(ref) else { return nil }
        return FogTileBits(words: tile.words)
    }
}
