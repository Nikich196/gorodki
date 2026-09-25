import DesignSystem
import Foundation
import Testing

/// Токены утверждённого дизайна (design/APPROVALS.md, 25.09.2026; docs/design/tokens.md) — данные без SwiftUI, поэтому
/// проверяются прямо по hex. Пакет GorodkiKit на Linux не собирается (в нём SwiftUI и ActivityKit), поэтому тесты —
/// здесь, в приложении на симуляторе (ios.yml), а не в ios-core.
@Suite("Дизайн-система: палитра, альфы, контраст, туман, движение")
struct DesignTokensTests {
    @Test("12 цветов игроков × 3 уровня × 2 темы: у каждого уровня свой цвет, альфа растёт с уровнем")
    func fillsComplete() {
        #expect(PlayerColor.allCases.count == 12)
        #expect(TerritoryLevel.allCases.map(\.rawValue) == [1, 2, 3])
        for color in PlayerColor.allCases {
            for theme in Theme.allCases {
                let fills = TerritoryLevel.allCases.map { color.fill($0, theme: theme) }
                #expect(Set(fills.map(\.hex)).count == 3, "\(color) \(theme): уровни одного цвета совпали")
                #expect(fills.map(\.alpha) == fills.map(\.alpha).sorted(), "\(color) \(theme): альфа не растёт")
                #expect(fills.allSatisfy { $0.alpha > 0 && $0.alpha <= 0.75 }, "\(color) \(theme)")
            }
            // Ночью L3 — ровно цвет «Старта»: уровень — насыщенность того же цвета.
            #expect(color.fill(.three, theme: .night).hex == color.hex, "\(color)")
        }
    }

    /// PLAN.md §6.5: «прозрачность 0,40 на светлой карте (0,55 для светлых цветов), 0,50 на тёмной» — это L2.
    /// L1 = ×0,62, L3 = ×1,4, но не больше 0,75 (docs/design/tokens.md, §2), до сотых.
    @Test("Альфы уровней — от базы PLAN §6.5: L2 = база, L1 = ×0,62, L3 = ×1,4 (≤ 0,75)")
    func alphasFollowPlan() {
        func hundredths(_ value: Double) -> Double { (value * 100).rounded() / 100 }
        for color in PlayerColor.allCases {
            for theme in Theme.allCases {
                let base = theme == .night ? 0.50 : (color.isLight ? 0.55 : 0.40)
                #expect(color.fill(.two, theme: theme).alpha == base, "\(color) \(theme)")
                #expect(color.fill(.one, theme: theme).alpha == hundredths(base * 0.62), "\(color) \(theme)")
                #expect(
                    color.fill(.three, theme: theme).alpha == min(hundredths(base * 1.4), 0.75), "\(color) \(theme)")
            }
        }
        #expect(PlayerColor.allCases.filter(\.isLight) == [.sun, .lime, .mint, .sky, .coral])
    }

    /// Для крупного текста WCAG требует 3:1, а не 4,5:1. Крупный — от 14 pt полужирного по WCAG, это 18,66 px;
    /// pt на iPhone — те же px. Надпись «Старта» — 21 pt Heavy.
    @Test(
        "Текст на «Старте» читается: контраст ≥ 3:1 у всех 12 цветов, надпись крупная", arguments: PlayerColor.allCases)
    func startInkContrast(_ color: PlayerColor) {
        let ratio = color.base.contrastRatio(to: color.startInk)
        #expect(ratio >= 3, "\(color): \(ratio)")
        #expect(TypeRole.startLabel.size >= 18.66)
        #expect(TypeRole.startLabel.weight == .heavy)
    }

    @Test("Кромка видна: днём ≥ 3:1 к земле, ночью ≥ 4,5:1", arguments: PlayerColor.allCases)
    func edgeContrast(_ color: PlayerColor) {
        #expect(color.edge.day.contrastRatio(to: Palette.mapLand.day) >= 3, "\(color) днём")
        #expect(color.edge.night.contrastRatio(to: Palette.mapLand.night) >= 4.5, "\(color) ночью")
    }

    /// Заметная разница в OKLab ≈ 2 (×100); соседние уровни должны отличаться хотя бы вдвое больше. Расчёт — смешение
    /// заливки с землёй в sRGB: так её рисует карта. Сейчас минимум 4,4 днём (Coral L1–L2) и 6,4 ночью.
    @Test("Соседние уровни различимы на земле: ΔE OKLab ≥ 4 в обеих темах")
    func levelsDistinguishable() {
        for theme in Theme.allCases {
            let land = Palette.mapLand[theme]
            for color in PlayerColor.allCases {
                let labs = TerritoryLevel.allCases.map { OKLab(color.fill($0, theme: theme), over: land) }
                for index in 0..<2 {
                    let difference = labs[index].distance(to: labs[index + 1])
                    #expect(difference >= 4, "\(color) \(theme) L\(index + 1)–L\(index + 2): \(difference)")
                }
            }
        }
    }

    @Test("Контраст по WCAG: чёрное к белому — 21:1, цвет к себе — 1:1; hex пишется с нулями")
    func contrastFormula() {
        #expect(abs(RGBA(0x000000).contrastRatio(to: 0xFFFFFF) - 21) < 1e-9)
        #expect(abs(RGBA(0xD30931).contrastRatio(to: 0xD30931) - 1) < 1e-9)
        #expect(RGBA(0x0A0E15).hexString == "#0A0E15")
        #expect(RGBA(0x00000F).hexString == "#00000F")
    }

    @Test("Туман: альфа дымки в пределах калибровки 0,6–0,8 (PLAN §6.4), своя палитра — не цвет игрока")
    func fog() {
        var players: Set<UInt32> = []
        for color in PlayerColor.allCases {
            players.formUnion([color.hex, color.edge.day.hex, color.edge.night.hex])
            for theme in Theme.allCases {
                players.formUnion(TerritoryLevel.allCases.map { color.fill($0, theme: theme).hex })
            }
        }
        for theme in Theme.allCases {
            #expect(FogStyle.calibrationRange.contains(FogStyle.haze[theme].alpha), "\(theme)")
            #expect(!players.contains(FogStyle.haze[theme].hex), "\(theme): дымка цвета игрока")
            #expect(!players.contains(FogStyle.edge[theme].hex), "\(theme): кромка тумана цвета игрока")
        }
        #expect(FogStyle.haze.day.alpha == 0.76)
        #expect(FogStyle.haze.night.alpha == 0.66)
        #expect(FogStyle.edgeBandWidth == 1.8)
    }

    @Test("Отношения — узором и толщиной: моё сплошное 2,4 pt, клан штрихован, спорное — муравьи, призрак — пунктир")
    func relations() {
        let levelFill = PlayerColor.orange.fill(.two, theme: .day)
        #expect(TerritoryRelation.mine.edge == EdgeStroke(width: 2.4))
        #expect(TerritoryRelation.mine.fill(levelFill: levelFill, theme: .day) == levelFill)
        #expect(TerritoryRelation.mine.glowRadius(theme: .night) == 3)
        #expect(TerritoryRelation.mine.glowRadius(theme: .day) == 0)
        #expect(TerritoryRelation.clan.hatch != nil)
        #expect(TerritoryRelation.allCases.filter { $0.hatch != nil } == [.clan])
        #expect(TerritoryRelation.rival.edge.width < TerritoryRelation.mine.edge.width)
        #expect(TerritoryRelation.rival.fill(levelFill: levelFill, theme: .day).alpha < levelFill.alpha)
        #expect(TerritoryRelation.allCases.filter(\.hasMarchingAnts) == [.contested])
        #expect(TerritoryRelation.contested.edge.dash == [4, 4])
        #expect(TerritoryRelation.contested.edge.haloWidth != nil)
        #expect(!TerritoryRelation.lost.edge.dash.isEmpty)
        // Штриховка клана не становится непрозрачной ни у одного цвета и уровня.
        for color in PlayerColor.allCases {
            for theme in Theme.allCases {
                for level in TerritoryLevel.allCases {
                    #expect(Hatch().color(levelFill: color.fill(level, theme: theme)).alpha <= 0.95)
                }
            }
        }
    }

    @Test("Движение: церемонии укладываются в 2 с (PLAN §6.8), шаги захвата идут по порядку")
    func motion() {
        let capture = MotionSpec.Capture.self
        #expect(capture.total <= MotionSpec.ceremonyLimit)
        #expect(capture.ringEnd <= MotionSpec.ceremonyLimit)
        #expect(capture.outlineDuration <= capture.fillEnd)
        #expect(capture.fillStart < capture.fillEnd)
        #expect(capture.estimateAt < capture.confirmedAt)
        #expect(capture.confirmedAt < capture.total)
        let badge = MotionSpec.BadgeDrop.self
        #expect(badge.glowEnd <= MotionSpec.ceremonyLimit)
        #expect(badge.glintEnd <= MotionSpec.ceremonyLimit)
        #expect(badge.textEnd <= MotionSpec.ceremonyLimit)
        #expect(MotionSpec.FogBreath.expandDuration < MotionSpec.FogBreath.period)
    }

    @Test("Редкости — формой: у каждого металла своя форма, глиф и градиенты")
    func metals() {
        #expect(Set(BadgeMetal.allCases.map(\.shape)).count == BadgeMetal.allCases.count)
        for metal in BadgeMetal.allCases {
            for stops in [metal.rim, metal.face, metal.progress] {
                #expect(stops.count >= 2, "\(metal)")
                #expect(stops.first?.location == 0 && stops.last?.location == 1, "\(metal)")
                #expect(stops.map(\.location) == stops.map(\.location).sorted(), "\(metal)")
            }
            #expect(metal.shape.faceRadius < metal.shape.rimRadius, "\(metal)")
            #expect(metal.shape.rimRadius <= 1, "\(metal)")
        }
        for kind in BadgeShapeKind.allCases {
            let radii = kind.unitVertices.map { ($0.x * $0.x + $0.y * $0.y).squareRoot() }
            #expect(radii.count >= 6 && radii.allSatisfy { $0 <= 1 + 1e-9 }, "\(kind)")
        }
        #expect(BadgeShapeKind.star.unitVertices.count == 16)
        #expect(BadgeShapeKind.hexagon.unitVertices.count == 6)
        #expect(BadgeShapeKind.octagon.unitVertices.count == 8)
    }
}

/// Цвет в OKLab (Björn Ottosson, 2020) после смешения с фоном в sRGB.
private struct OKLab {
    let lightness: Double
    let a: Double
    let b: Double

    init(_ color: RGBA, over background: RGBA) {
        func mix(_ top: Double, _ bottom: Double) -> Double { color.alpha * top + (1 - color.alpha) * bottom }
        func linear(_ channel: Double) -> Double {
            channel <= 0.040_45 ? channel / 12.92 : pow((channel + 0.055) / 1.055, 2.4)
        }
        let red = linear(mix(color.red, background.red))
        let green = linear(mix(color.green, background.green))
        let blue = linear(mix(color.blue, background.blue))
        let l = cbrt(0.412_221_470_8 * red + 0.536_332_536_3 * green + 0.051_445_992_9 * blue)
        let m = cbrt(0.211_903_498_2 * red + 0.680_699_545_1 * green + 0.107_396_956_6 * blue)
        let s = cbrt(0.088_302_461_9 * red + 0.281_718_837_6 * green + 0.629_978_700_5 * blue)
        lightness = 0.210_454_255_3 * l + 0.793_617_785_0 * m - 0.004_072_046_8 * s
        a = 1.977_998_495_1 * l - 2.428_592_205_0 * m + 0.450_593_709_9 * s
        b = 0.025_904_037_1 * l + 0.782_771_766_2 * m - 0.808_675_766_0 * s
    }

    /// ΔE ×100 — в тех же единицах, что docs/design/tokens.md.
    func distance(to other: OKLab) -> Double {
        let lightnessDifference = lightness - other.lightness
        let aDifference = a - other.a
        let bDifference = b - other.b
        return 100
            * (lightnessDifference * lightnessDifference + aDifference * aDifference + bDifference * bDifference)
            .squareRoot()
    }
}
