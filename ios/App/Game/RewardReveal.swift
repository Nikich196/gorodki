import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// Награда для показа: открылось достижение, найден тайник, взята корона (дизайн-проход 16.10: «чтобы карточка
/// не просто появлялась, а была целая анимация, с вибрацией и звуком»).
struct Reward: Identifiable, Equatable, Sendable {
    /// «achievement:first-run», «cache:…», «crown:…» — по нему награда показывается один раз.
    let id: String
    let symbol: String
    let color: Color
    /// «ДОСТИЖЕНИЕ», «ЭПИЧЕСКИЙ ТАЙНИК», «КОРОНА».
    let label: String
    let title: String
    let subtitle: String
}

extension Reward {
    /// Образец для фикстуры `reveal`: эпический тайник.
    static let sample = Reward(
        id: "cache:sample", symbol: "star.circle.fill", color: Components.Schemas.BadgeRarity.epic.color,
        label: "ЭПИЧЕСКИЙ ТАЙНИК", title: "Мост над Мухавцом", subtitle: "Ты нашёл его первым — золотая рамка")
}

/// Какие награды уже показаны — `UserDefaults`, своя запись на вид. Первый раз вид только запоминает текущие
/// награды без показа: иначе после обновления на игрока высыпались бы все старые сразу.
enum SeenRewards {
    static let key = "rewards.seen"

    static func unseen(_ rewards: [Reward], kind: String, defaults: UserDefaults = .standard) -> [Reward] {
        var seen = Set(defaults.stringArray(forKey: key) ?? [])
        let baseline = "\(key).\(kind)"
        guard defaults.bool(forKey: baseline) else {
            seen.formUnion(rewards.map(\.id))
            defaults.set(Array(seen), forKey: key)
            defaults.set(true, forKey: baseline)
            return []
        }
        return rewards.filter { !seen.contains($0.id) }
    }

    static func markSeen(_ id: String, defaults: UserDefaults = .standard) {
        var seen = Set(defaults.stringArray(forKey: key) ?? [])
        seen.insert(id)
        defaults.set(Array(seen), forKey: key)
    }
}

/// Показ награды поверх всего: шар света → карточка рубашкой → разворот в 3D со вспышкой, лучами, искрами, ударом
/// вибрации и аккордом. «Уменьшить движение» — карточка просто проявляется.
struct RewardRevealView: View {
    let reward: Reward
    let done: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.brandSound) private var sound
    @State private var appeared = false
    @State private var angle: Double = 180
    @State private var burst = 0
    @State private var settled = false

    var body: some View {
        ZStack {
            Rectangle()
                .fill(.ultraThinMaterial)
                .overlay(Color.black.opacity(0.6))
                .ignoresSafeArea()
                .opacity(appeared ? 1 : 0)
                .onTapGesture { if settled { finish() } }
            // Лучи — наложением: их 720 pt не должны раздвигать экран.
            Color.clear
                .overlay {
                    RewardRays(color: reward.color)
                        .opacity(angle < 90 ? 1 : 0)
                        .scaleEffect(angle < 90 ? 1 : 0.6)
                        .animation(.easeOut(duration: 0.6), value: angle < 90)
                }
                .allowsHitTesting(false)
            FlipCard(angle: angle, front: front, back: back)
                .frame(width: 270, height: 360)
                .scaleEffect(appeared ? 1 : 0.15)
                .opacity(appeared ? 1 : 0)
            ConfettiBurst(trigger: burst, colors: [reward.color, Brand.amber.night.color, .white], style: .sparks)
                .allowsHitTesting(false)
            ConfettiBurst(trigger: burst, colors: [reward.color, Brand.ember.night.color, Brand.dusk.night.color])
                .allowsHitTesting(false)
            VStack {
                Spacer()
                Button("Забрать", action: finish)
                    .buttonStyle(.brand)
                    .padding(.horizontal, 40)
                    .padding(.bottom, 30)
                    .opacity(settled ? 1 : 0)
                    .offset(y: settled ? 0 : 30)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(reward.label): \(reward.title). \(reward.subtitle)")
        .task { await play() }
    }

    /// Хореография: 0 — шар, 0,15 — карточка рубашкой, 0,8 — разворот (удар, аккорд, искры), 1,3 — «Забрать».
    private func play() async {
        if reduceMotion {
            angle = 0
            withAnimation(.easeOut(duration: 0.3)) { appeared = true }
            sound.play(.reveal)
            Haptics.shared.capture()
            settled = true
            return
        }
        Haptics.shared.reveal()
        sound.play(.reveal)
        withAnimation(.spring(duration: 0.5, bounce: 0.35)) { appeared = true }
        try? await Task.sleep(for: .milliseconds(650))
        withAnimation(.spring(duration: 0.6, bounce: 0.25)) { angle = 0 }
        try? await Task.sleep(for: .milliseconds(170))
        burst += 1
        try? await Task.sleep(for: .milliseconds(500))
        withAnimation(.spring(duration: 0.5)) { settled = true }
    }

    private func finish() {
        withAnimation(.easeIn(duration: 0.25)) {
            appeared = false
            settled = false
        }
        Task {
            try? await Task.sleep(for: .milliseconds(260))
            done()
        }
    }

    private var front: some View {
        VStack(spacing: 14) {
            Text(reward.label)
                .font(.system(size: 12, weight: .heavy).width(.expanded))
                .kerning(1.2)
                .foregroundStyle(reward.color)
            ZStack {
                Circle()
                    .fill(
                        RadialGradient(
                            colors: [reward.color.mix(with: .white, by: 0.4), reward.color], center: .topLeading,
                            startRadius: 4, endRadius: 110)
                    )
                    .shadow(color: reward.color.opacity(0.6), radius: 18)
                Image(systemName: reward.symbol)
                    .font(.system(size: 56, weight: .bold))
                    .foregroundStyle(.white)
                    .symbolEffect(.bounce, value: burst)
            }
            .frame(width: 130, height: 130)
            Text(reward.title)
                .font(.system(size: 24, weight: .black).width(.expanded))
                .italic()
                .multilineTextAlignment(.center)
                .foregroundStyle(.white)
                .minimumScaleFactor(0.7)
            Text(reward.subtitle)
                .font(.subheadline)
                .multilineTextAlignment(.center)
                .foregroundStyle(.white.opacity(0.8))
        }
        .padding(22)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RewardCardBackground(color: reward.color))
        .overlay(ShimmerPass(active: burst > 0).clipShape(.rect(cornerRadius: 30, style: .continuous)))
        .clipShape(.rect(cornerRadius: 30, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 30, style: .continuous)
                .strokeBorder(
                    LinearGradient(
                        colors: [.white.opacity(0.85), reward.color.opacity(0.4), .white.opacity(0.3)],
                        startPoint: .topLeading, endPoint: .bottomTrailing), lineWidth: 2)
        }
    }

    private var back: some View {
        ZStack {
            RewardCardBackground(color: Brand.dusk.night.color)
            FortressStar()
                .stroke(.white.opacity(0.12), lineWidth: 1.5)
                .padding(30)
            BrandMark(size: 92)
        }
        .clipShape(.rect(cornerRadius: 30, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 30, style: .continuous).strokeBorder(.white.opacity(0.35), lineWidth: 2)
        }
        .shadow(color: reward.color.opacity(0.5), radius: 24)
    }
}

/// Карточка с двумя сторонами: угол 180° — рубашка, 0° — лицо. Сторона меняется ровно на ребре (90°).
private struct FlipCard<Front: View, Back: View>: View, Animatable {
    var angle: Double
    let front: Front
    let back: Back

    var animatableData: Double {
        get { angle }
        set { angle = newValue }
    }

    var body: some View {
        ZStack {
            front.opacity(angle < 90 ? 1 : 0)
            back.opacity(angle < 90 ? 0 : 1)
                .rotation3DEffect(.degrees(180), axis: (x: 0, y: 1, z: 0))
        }
        .rotation3DEffect(.degrees(angle), axis: (x: 0, y: 1, z: 0), perspective: 0.55)
    }
}

/// Фон лица карточки: ночное небо, свечение цвета награды снизу.
private struct RewardCardBackground: View {
    let color: Color

    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(hex: 0x231A5C), Color(hex: 0x0B0C22)], startPoint: .top, endPoint: .bottom)
            RadialGradient(
                colors: [color.opacity(0.65), .clear], center: .bottom, startRadius: 4, endRadius: 260)
        }
    }
}

/// Лучи за карточкой: вращающийся «веер» цвета награды.
private struct RewardRays: View {
    let color: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(paused: reduceMotion)) { timeline in
            let turn =
                reduceMotion ? 0 : timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 24) * 15
            AngularGradient(
                gradient: Gradient(
                    colors: (0..<16).map { $0.isMultiple(of: 2) ? color.opacity(0.75) : .clear }),
                center: .center, angle: .degrees(turn)
            )
            .mask(RadialGradient(colors: [.white, .clear], center: .center, startRadius: 40, endRadius: 360))
            .frame(width: 720, height: 720)
            .blur(radius: 6)
        }
    }
}

/// Блик, пробегающий по лицу карточки один раз после разворота.
private struct ShimmerPass: View {
    let active: Bool
    @State private var offset: CGFloat = -1

    var body: some View {
        GeometryReader { proxy in
            LinearGradient(
                colors: [.clear, .white.opacity(0.45), .clear], startPoint: .leading, endPoint: .trailing
            )
            .frame(width: proxy.size.width * 0.5)
            .rotationEffect(.degrees(20))
            .offset(x: offset * proxy.size.width * 1.5)
        }
        .allowsHitTesting(false)
        .onChange(of: active) {
            guard active else { return }
            offset = -1
            withAnimation(.easeInOut(duration: 0.9).delay(0.1)) { offset = 1 }
        }
    }
}
