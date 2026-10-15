import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// Серия в профиле (PLAN.md, §3.7: «серия с заморозкой»): огонь с днями, засчитан ли сегодня, заморозка. День серии —
/// сутки по Минску с забегами от 1 км (сервер, `StreakRules`); засчитывается через ~25 минут после финиша.
struct StreakCard: View {
    let streak: StreakInfo
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let theme = Theme(colorScheme)
        let days = Int(streak.days)
        HStack(spacing: 16) {
            ZStack {
                Circle()
                    .fill(days > 0 ? AnyShapeStyle(Brand.fire(theme)) : AnyShapeStyle(Palette.uiCell2.color))
                    .shadow(color: days > 0 ? Brand.ember[theme].color.opacity(0.5) : .clear, radius: 12, y: 4)
                Image(systemName: "flame.fill")
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(days > 0 ? .white : Palette.uiInk3.color)
                    .symbolEffect(.bounce, options: .repeat(.periodic(delay: 5)), isActive: days > 0 && !reduceMotion)
            }
            .frame(width: 60, height: 60)
            .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(days > 0 ? CountText.days(days) + " подряд" : "Начни серию")
                    .font(.system(size: 22, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(Palette.uiInk.color)
                    .contentTransition(.numericText())
                Text(hint)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .fixedSize(horizontal: false, vertical: true)
                if streak.freezeActive {
                    Label("Заморозка: один пропуск не сожжёт серию", systemImage: "snowflake")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(PlayerColor.sky.edge[theme].color)
                }
            }
            Spacer(minLength: 0)
            if streak.todayCounted {
                Image(systemName: "checkmark.seal.fill")
                    .font(.title2)
                    .foregroundStyle(PlayerColor.forest.edge[theme].color)
                    .accessibilityLabel("Сегодня засчитан")
            }
        }
        .brandCard(padding: 16)
        .accessibilityElement(children: .combine)
    }

    private var hint: String {
        if streak.days == 0 { return "Пройди сегодня хотя бы 1 км — и огонь загорится." }
        if streak.todayCounted { return "Сегодня засчитан — до завтра." }
        return "Пройди 1 км до полуночи — иначе серия сгорит."
    }
}

/// Раздел «Игра» в профиле: серия карточкой, дальше — «Короли», дуэли, «Рюкзак», «Коллекция», карточка недели,
/// Зал славы. Раньше здесь был список «скоро» (решение Никиты 15.10: «всё открыто и работает сразу»).
struct GameHubSection: View {
    /// Профиль — числа для «Достижений» (забеги, километры, туман).
    let profile: ProfileModel
    @Environment(SocialScreens.self) private var social

    var body: some View {
        let game = social.game
        VStack(alignment: .leading, spacing: 10) {
            if let streak = game.streak.value {
                StreakCard(streak: streak)
            }
            Text("Игра")
                .font(.headline)
                .foregroundStyle(Palette.uiInk.color)
                .padding(.top, 4)
            VStack(spacing: 0) {
                row("Достижения", "Значки за путь, туман, короны и тайники", "medal.fill", Brand.ember.day.color) {
                    AchievementsScreen(profile: profile)
                }
                divider
                row("Короли участков", "Короны за быстрые отрезки", "crown.fill", Brand.amber.day.color) {
                    KingsScreen()
                }
                divider
                row("Дуэли", game.duelsSummary ?? "Вызов другу 1×1", "figure.fencing", Brand.ember.day.color) {
                    DuelsScreen()
                }
                divider
                row(
                    "Рюкзак", game.inventorySummary ?? "Фишки: «Радар», «Магнит», «Заморозка»", "backpack.fill",
                    Brand.dusk.day.color
                ) {
                    BackpackScreen()
                }
                divider
                row(
                    "Коллекция", game.collectionSummary ?? "Тайники Бреста и значки", "rosette",
                    Components.Schemas.BadgeRarity.legendary.color
                ) {
                    CollectionScreen()
                }
                divider
                row("Карточка недели", "Итог недели картинкой", "photo.on.rectangle.angled", Brand.river.day.color) {
                    WeeklyCardScreen()
                }
                divider
                row("Зал славы", "Лучшие каждого сезона", "trophy.fill", PlayerColor.sun.edge.day.color) {
                    HallOfFameScreen()
                }
            }
            .brandCard(padding: 0)
        }
        .task {
            async let overview: Void = game.loadOverview()
            async let clan: Void = social.clan.load()
            _ = await (overview, clan)
        }
        // Новые достижения — карточкой с анимацией (`RewardRevealView`), когда ответили все источники.
        .onChange(of: unlockedAchievements, initial: true) { _, unlocked in
            guard AchievementInputs.ready(profile: profile, social: social) else { return }
            game.offer(unlocked.map(\.reward), kind: "achievements")
        }
    }

    private var unlockedAchievements: [Achievement] {
        guard AchievementInputs.ready(profile: profile, social: social) else { return [] }
        return Achievements.evaluate(.current(profile: profile, social: social)).filter(\.unlocked)
    }

    private var divider: some View {
        Divider().padding(.leading, 66)
    }

    private func row<Destination: View>(
        _ title: String, _ detail: String, _ symbol: String, _ color: Color,
        @ViewBuilder destination: @escaping () -> Destination
    ) -> some View {
        NavigationLink {
            destination()
        } label: {
            HStack(spacing: 14) {
                GameBadge(systemImage: symbol, color: color, size: 38)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Palette.uiInk.color)
                    Text(detail)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                        .lineLimit(1)
                }
                Spacer(minLength: 8)
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Palette.uiInk3.color)
            }
            .padding(.horizontal, 14)
            .frame(minHeight: 62)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }
}
