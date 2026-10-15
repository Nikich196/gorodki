import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// Числа, из которых считаются достижения: профиль (`GET /me/stats`, `/fog/summary`), игровые разделы и клан. `nil` —
/// ещё не знаем (не загрузилось) — достижение показывается закрытым, без ложного прогресса.
struct AchievementInputs: Equatable {
    var runs: Int?
    var distanceMeters: Double?
    var exploredSquareMeters: Double?
    var brestPercent: Double?
    var arenaPercent: Double?
    var streakDays: Int?
    var crowns: Int?
    var cachesFound: Int?
    var duelWins: Int?
    var inClan: Bool?
}

/// Достижение (PLAN.md, §3.7: «значки за достижения»; §3.10: «100 % Арены»): открыто или прогресс к нему.
struct Achievement: Identifiable, Equatable {
    let id: String
    let title: String
    let detail: String
    let symbol: String
    let rarity: Components.Schemas.BadgeRarity
    /// 0…1; открыто — 1.
    let progress: Double
    /// «3,2 из 10 км»; открыто — что сделано.
    let progressText: String

    var unlocked: Bool { progress >= 1 }
}

/// Достижения считаются на телефоне из уже загруженных чисел — своего адреса у них нет (решено 16.10: вместо
/// «Достижения — скоро» в статистике). Пороги — свои, их можно поменять здесь.
enum Achievements {
    static func evaluate(_ input: AchievementInputs) -> [Achievement] {
        [
            count(
                "first-run", "Первый шаг", "Первый забег в «Городках»", "figure.run", .common, input.runs, goal: 1),
            meters("ten-km", "Десятка", "10 км засчитанного пути", "10.circle.fill", .common, input.distanceMeters, 10),
            meters("marathon", "Марафон", "42,2 км засчитанного пути", "medal.fill", .rare, input.distanceMeters, 42.2),
            area(
                "hectare", "Первый гектар", "1 га открытого тумана", "cloud.sun.fill", .common,
                input.exploredSquareMeters),
            percent("brest-1", "Первооткрыватель", "1 % Бреста открыт", "map.fill", .rare, input.brestPercent, goal: 1),
            percent(
                "arena-100", "Вся Арена", "100 % Арены БрГТУ открыто", "building.columns.fill", .legendary,
                input.arenaPercent, goal: 100),
            count(
                "streak-7", "Неделя огня", "Серия 7 дней подряд", "flame.fill", .rare, input.streakDays, goal: 7),
            count(
                "king", "Король участка", "Корона на отрезке", "crown.fill", .epic, input.crowns, goal: 1),
            count(
                "cache", "Кладоискатель", "Первый найденный тайник", "rosette", .rare, input.cachesFound, goal: 1),
            count(
                "duel", "Дуэлянт", "Первая победа в дуэли", "figure.fencing", .rare, input.duelWins, goal: 1),
            Achievement(
                id: "clan", title: "Свой клан", detail: "Вступи в клан или создай свой", symbol: "shield.fill",
                rarity: .common, progress: input.inClan == true ? 1 : 0,
                progressText: input.inClan == true ? "в клане" : "пока без клана"),
        ]
    }

    private static func count(
        _ id: String, _ title: String, _ detail: String, _ symbol: String, _ rarity: Components.Schemas.BadgeRarity,
        _ value: Int?, goal: Int
    ) -> Achievement {
        let have = value ?? 0
        return Achievement(
            id: id, title: title, detail: detail, symbol: symbol, rarity: rarity,
            progress: min(1, Double(have) / Double(goal)),
            progressText: value == nil ? "—" : "\(min(have, goal)) из \(goal)")
    }

    private static func meters(
        _ id: String, _ title: String, _ detail: String, _ symbol: String, _ rarity: Components.Schemas.BadgeRarity,
        _ value: Double?, _ goalKilometers: Double
    ) -> Achievement {
        let have = (value ?? 0) / 1_000
        let text =
            value == nil
            ? "—"
            : NumberText.decimal(min(have, goalKilometers), fractionDigits: 1) + " из "
                + NumberText.decimal(goalKilometers, fractionDigits: goalKilometers.rounded() == goalKilometers ? 0 : 1)
                + NumberText.unitSeparator + "км"
        return Achievement(
            id: id, title: title, detail: detail, symbol: symbol, rarity: rarity,
            progress: min(1, have / goalKilometers), progressText: text)
    }

    private static func area(
        _ id: String, _ title: String, _ detail: String, _ symbol: String, _ rarity: Components.Schemas.BadgeRarity,
        _ value: Double?
    ) -> Achievement {
        let have = value ?? 0
        let text =
            value == nil
            ? "—"
            : NumberText.hectares(fromSquareMeters: min(have, 10_000), fractionDigits: 2) + " из 1"
                + NumberText.unitSeparator + "га"
        return Achievement(
            id: id, title: title, detail: detail, symbol: symbol, rarity: rarity, progress: min(1, have / 10_000),
            progressText: text)
    }

    private static func percent(
        _ id: String, _ title: String, _ detail: String, _ symbol: String, _ rarity: Components.Schemas.BadgeRarity,
        _ value: Double?, goal: Double
    ) -> Achievement {
        let have = value ?? 0
        let text =
            value == nil
            ? "—"
            : NumberText.decimal(min(have, goal), fractionDigits: 1) + " из "
                + NumberText.decimal(goal, fractionDigits: 0) + NumberText.unitSeparator + "%"
        return Achievement(
            id: id, title: title, detail: detail, symbol: symbol, rarity: rarity, progress: min(1, have / goal),
            progressText: text)
    }
}

/// Экран «Достижения»: открытые — значок цвета редкости, закрытые — силуэт и кольцо прогресса.
struct AchievementsScreen: View {
    let profile: ProfileModel
    @Environment(SocialScreens.self) private var social

    private let columns = [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)]

    var body: some View {
        let list = Achievements.evaluate(inputs)
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Открыто \(list.filter(\.unlocked).count) из \(list.count)")
                    .font(.system(size: 24, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(Palette.uiInk.color)
                LazyVGrid(columns: columns, spacing: 12) {
                    ForEach(list) { achievement in
                        AchievementTile(achievement: achievement)
                    }
                }
            }
            .padding(16)
        }
        .atmosphereBackground()
        .navigationTitle("Достижения")
        .task {
            let game = social.game
            async let overview: Void = game.loadOverview()
            async let segments: Void = game.loadSegments()
            async let clan: Void = social.clan.load()
            _ = await (overview, segments, clan)
        }
    }

    private var inputs: AchievementInputs {
        AchievementInputs.current(profile: profile, social: social)
    }
}

private struct AchievementTile: View {
    let achievement: Achievement

    var body: some View {
        let color = achievement.rarity.color
        VStack(spacing: 8) {
            ZStack {
                Circle()
                    .stroke(Palette.uiInk3.color.opacity(0.18), lineWidth: 5)
                Circle()
                    .trim(from: 0, to: achievement.progress)
                    .stroke(color, style: StrokeStyle(lineWidth: 5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Image(systemName: achievement.symbol)
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(achievement.unlocked ? .white : Palette.uiInk3.color)
                    .frame(width: 58, height: 58)
                    .background {
                        if achievement.unlocked {
                            Circle().fill(
                                LinearGradient(
                                    colors: [color.mix(with: .white, by: 0.3), color], startPoint: .topLeading,
                                    endPoint: .bottomTrailing))
                        }
                    }
                    .shadow(color: achievement.unlocked ? color.opacity(0.45) : .clear, radius: 8, y: 3)
            }
            .frame(width: 74, height: 74)
            Text(achievement.title)
                .font(.subheadline.weight(.bold))
                .foregroundStyle(Palette.uiInk.color)
            Text(achievement.detail)
                .font(.caption)
                .foregroundStyle(Palette.uiInk2.color)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            Text(achievement.unlocked ? achievement.rarity.title.uppercased() : achievement.progressText)
                .font(.system(size: 10, weight: .heavy).width(.expanded).monospacedDigit())
                .foregroundStyle(achievement.unlocked ? color : Palette.uiInk2.color)
        }
        .frame(maxWidth: .infinity, minHeight: 196, alignment: .top)
        .brandCard(padding: 12)
        .accessibilityElement(children: .combine)
        .accessibilityValue(achievement.unlocked ? "открыто" : achievement.progressText)
    }
}

extension AchievementInputs {
    /// Числа из профиля, игровых разделов и клана — то, что приложение уже загрузило.
    @MainActor
    static func current(profile: ProfileModel, social: SocialScreens) -> AchievementInputs {
        let game = social.game
        let arena = profile.exploration?.allTime.districts?.first { $0.name.contains("Арена") }
        return AchievementInputs(
            runs: profile.runs, distanceMeters: profile.distanceMeters,
            exploredSquareMeters: profile.exploredSquareMeters,
            brestPercent: profile.exploration?.allTime.brestPercent, arenaPercent: arena?.percent,
            streakDays: game.streak.value.map { Int($0.days) },
            crowns: game.segments.value.map { $0.segments.filter { $0.seasonCrown?.me == true }.count },
            cachesFound: game.collection.value.map { $0.caches.filter { $0.foundAtMs != nil }.count },
            duelWins: game.duels.value.map { duels in
                duels.filter { $0.status == .finished && $0.winnerId == $0.me.playerId }.count
            },
            inClan: inClan(social.clan.state))
    }

    /// Все источники ответили (загрузились или нет) — можно сравнивать с прошлым и показывать новые награды.
    @MainActor
    static func ready(profile: ProfileModel, social: SocialScreens) -> Bool {
        let game = social.game
        let answered: [Bool] = [
            game.streak.isSettled, game.segments.isSettled, game.collection.isSettled, game.duels.isSettled,
        ]
        return answered.allSatisfy(\.self) && profile.exploredSquareMeters != nil && social.clan.state != .loading
    }

    /// В клане ли: пока клан грузится или не загрузился — неизвестно.
    private static func inClan(_ state: ClanModel.State) -> Bool? {
        switch state {
        case .member: true
        case .none: false
        case .loading, .failed: nil
        }
    }
}

extension Achievement {
    /// Карточка награды для показа (`RewardRevealView`).
    var reward: Reward {
        Reward(
            id: "achievement:\(id)", symbol: symbol, color: rarity.color,
            label: "ДОСТИЖЕНИЕ · " + rarity.title.uppercased(), title: title, subtitle: detail)
    }
}
