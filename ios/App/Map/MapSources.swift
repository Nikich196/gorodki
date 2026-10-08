import Foundation
import GameCore
import GorodkiAPI
import Networking

extension LandTile {
    /// Тайл ответа `/territory` (или кэша земли) → земля карты: кольца `[широта, долгота, …]` — в координаты, кромки —
    /// без сторон по краю тайла (`LandBorders`). Одна и та же дорога у живых данных и у образцов режима фикстур.
    init(
        key: LandTileKey, parcels: [Components.Schemas.ParcelView], zones: [Components.Schemas.ContestedZoneView]
    ) {
        self.init(
            key: key,
            parcels: parcels.map { view in
                LandParcel(
                    id: view.id, ownerId: view.ownerId, colorIndex: view.colorIndex, level: view.level,
                    ghost: view.ghost, lastVisitAtMs: view.lastVisitAtMs, shieldUntilMs: view.shieldUntilMs,
                    siegeUntilMs: view.siegeUntilMs,
                    shape: ParcelShape(
                        exterior: ParcelShape.ring(view.exterior), holes: view.holes.map(ParcelShape.ring)),
                    tile: key)
            },
            contestedZones: zones.map { zone in
                ContestedZone(
                    untilMs: zone.untilMs,
                    shape: ParcelShape(
                        exterior: ParcelShape.ring(zone.exterior), holes: zone.holes.map(ParcelShape.ring)))
            })
    }

    init(_ tile: Components.Schemas.TileTerritory) {
        self.init(
            key: LandTileKey(x: Int(tile.x), y: Int(tile.y)), parcels: tile.parcels, zones: tile.contestedZones)
    }
}

/// Живые данные карты: земля «Бега» (`TerritoryCache`) и свой туман «Пешком» за всё время (`FogCache`) из
/// `AppDependencies`. Кэши сами решают, что перезапросить (версии, подсказки, запасной опрос), и переживают
/// перезапуск на диске; здесь — только перевод в типы карты, вне главного потока.
struct CachedMapData: MapDataSource {
    let territory: TerritoryCache?
    let fogCache: FogCache?

    func land(visible: Set<LandTileKey>, known: Set<LandTileKey>) async throws -> [LandTile] {
        guard let territory else { return [] }
        let keys = Set(visible.map { TileKey(x: $0.x, y: $0.y) })
        // Сеть не ответила — перечитать все видимые: часть пачек до ошибки могла обновиться.
        let updated = try? await territory.refresh(visible: keys)
        var cached: [TerritoryCache.Tile] = []
        for key in keys.sorted() {
            let fresh = updated.map { $0.contains(key) } ?? true
            if fresh || !known.contains(LandTileKey(x: key.x, y: key.y)), let tile = await territory.tile(key) {
                cached.append(tile)
            }
        }
        let tiles = cached
        return await Task.detached(priority: .userInitiated) {
            tiles.map { tile in
                LandTile(
                    key: LandTileKey(x: tile.key.x, y: tile.key.y), parcels: tile.parcels,
                    zones: tile.contestedZones)
            }
        }.value
    }

    func fog(visible: Set<FogTileKey>, known: Set<FogTileKey>) async throws -> [FogTileKey: FogTileBits] {
        guard let fogCache else { return [:] }
        let refs = Set(visible.map { FogTileRef(x: $0.x, y: $0.y) })
        let updated = try? await fogCache.refresh(visible: refs)
        var result: [FogTileKey: FogTileBits] = [:]
        for ref in refs {
            let key = FogTileKey(x: ref.x, y: ref.y)
            let fresh = updated.map { $0.contains(ref) } ?? true
            guard fresh || !known.contains(key), let tile = await fogCache.tile(ref),
                let bits = FogTileBits(words: tile.words)
            else { continue }
            result[key] = bits
        }
        return result
    }
}

/// Ник владельца с сервера: `GET /players/{id}` — ник с его согласия, иначе «Игрок #1234».
struct APIPlayerNames: PlayerNames {
    let api: any APIProtocol

    func name(of playerId: String) async throws -> String {
        try await api.getPlayer(path: .init(id: playerId)).ok.body.json.name
    }
}

extension MapModel {
    /// Карта приложения: кэши и клиент API из `AppDependencies`.
    static func live(profile: ProfileModel, home: HomeModel? = nil) -> MapModel {
        let dependencies = AppDependencies.shared
        return MapModel(
            profile: profile,
            data: CachedMapData(territory: dependencies.territory, fogCache: dependencies.fog),
            names: dependencies.api.map { APIPlayerNames(api: $0) }, home: home)
    }
}
