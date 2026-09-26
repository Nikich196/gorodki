/// Круг «Дома» на карте «Исследования» (PLAN.md, §3.10, «Старт „от дома“»): вокруг точки «Дом» открыт круг ~500 м.
///
/// **Только для показа.** Точка и круг живут на телефоне: сервер их не знает, в статистику и «+N га» они не идут
/// (`DELETE /fog` их тоже не трогает). Поэтому круг не смешивается с туманом сервера, а накладывается поверх при
/// отрисовке: `display(_:home:)` возвращает новую карту клеток, свой туман остаётся как пришёл.
public enum HomeCircle {
    /// Радиус круга, метры: «вокруг открывается круг ~500 м» (§3.10) — радиус, как у зоны приватности «(400 м)».
    public static let radiusMeters = 500.0

    /// Клетки круга «Дома» по тайлам z14 — той же сеткой G22, что туман сервера: клетка открыта, если её центр в круге.
    public static func fog(around home: Coordinate, radius: Double = radiusMeters) -> [FogTileKey: FogTileBits] {
        guard home.isValid, radius > 0 else { return [:] }
        var layer = FogLayer()
        layer.reveal(around: home, radius: radius)
        return layer.tiles
    }

    /// Туман для карты: свой туман сервера плюс круг «Дома». Тайлы, которых нет у сервера, берутся из круга как есть.
    public static func display(
        _ fog: [FogTileKey: FogTileBits], home: [FogTileKey: FogTileBits]
    ) -> [FogTileKey: FogTileBits] {
        guard !home.isEmpty else { return fog }
        var result = fog
        for (key, bits) in home {
            result[key, default: FogTileBits()].formUnion(bits)
        }
        return result
    }
}
