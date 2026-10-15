import AVFoundation
import CoreHaptics
import DesignSystem
import UIKit

/// Звуки бренда «Крепость» (Никита 15.10: «добавь звуки в тему»): короткие синтезированные WAV из
/// `App/Resources/Sounds` (`ios/scripts/make-sounds.py`). Тихие, уважают беззвучный режим (категория `ambient`) и не
/// перебивают музыку; во время голосовой подсказки забега категорию держит она (`SystemRunFeedback.speak`), звук
/// просто подмешивается. Выключаются в «Настройках» («Звуки»).
@MainActor
final class SoundBoard {
    static let shared = SoundBoard()
    static let enabledKey = "sounds.enabled"

    private var players: [BrandSound: AVAudioPlayer] = [:]

    var enabled: Bool {
        get { UserDefaults.standard.object(forKey: Self.enabledKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: Self.enabledKey) }
    }

    /// Для окружения SwiftUI (`\.brandSound`).
    var player: BrandSoundPlayer {
        BrandSoundPlayer { sound in SoundBoard.shared.play(sound) }
    }

    func play(_ sound: BrandSound) {
        guard enabled else { return }
        let session = AVAudioSession.sharedInstance()
        if session.category == .soloAmbient {
            // По умолчанию iOS глушит чужую музыку — звукам игры это не нужно.
            try? session.setCategory(.ambient, options: [.mixWithOthers])
        }
        guard let player = players[sound] ?? load(sound) else { return }
        player.currentTime = 0
        player.play()
    }

    private func load(_ sound: BrandSound) -> AVAudioPlayer? {
        guard
            let url = Bundle.main.url(forResource: sound.rawValue, withExtension: "wav")
                ?? Bundle.main.url(forResource: sound.rawValue, withExtension: "wav", subdirectory: "Sounds"),
            let player = try? AVAudioPlayer(contentsOf: url)
        else { return nil }
        player.volume = Self.volume(sound)
        player.prepareToPlay()
        players[sound] = player
        return player
    }

    /// Щелчки и «свуш» — тише, события — громче.
    static func volume(_ sound: BrandSound) -> Float {
        switch sound {
        case .tap, .toggle: 0.35
        case .swoosh: 0.45
        case .chime: 0.55
        case .start, .capture, .rank: 0.8
        }
    }
}

/// Рисунки вибрации, которых нет в SwiftUI (`sensoryFeedback`): старт — три удара, как сигналы на стадионе, захват —
/// нарастающая волна и удар в конце. Нет Core Haptics (симулятор, старые модели) — обычная вибрация уведомления.
@MainActor
final class Haptics {
    static let shared = Haptics()

    private var engine: CHHapticEngine?

    /// Старт забега: два коротких удара и сильный — в такт звуку `start`.
    func start() {
        play(
            [
                transient(at: 0, intensity: 0.6, sharpness: 0.5),
                transient(at: 0.32, intensity: 0.6, sharpness: 0.5),
                transient(at: 0.64, intensity: 1, sharpness: 0.9),
            ], fallback: .success)
    }

    /// Захват подтверждён: волна нарастает 0,4 с и обрывается сильным ударом.
    func capture() {
        let swell = CHHapticEvent(
            eventType: .hapticContinuous,
            parameters: [
                CHHapticEventParameter(parameterID: .hapticIntensity, value: 0.6),
                CHHapticEventParameter(parameterID: .hapticSharpness, value: 0.3),
            ], relativeTime: 0, duration: 0.42)
        let curve = CHHapticParameterCurve(
            parameterID: .hapticIntensityControl,
            controlPoints: [
                .init(relativeTime: 0, value: 0.2), .init(relativeTime: 0.4, value: 1),
            ], relativeTime: 0)
        play(
            [
                swell, transient(at: 0.44, intensity: 1, sharpness: 1),
                transient(at: 0.6, intensity: 0.5, sharpness: 0.6),
            ],
            curves: [curve], fallback: .success)
    }

    /// Пьедестал: три мягких толчка — столбики встают.
    func podium() {
        play(
            [
                transient(at: 0.15, intensity: 0.4, sharpness: 0.3),
                transient(at: 0.3, intensity: 0.6, sharpness: 0.4),
                transient(at: 0.45, intensity: 0.9, sharpness: 0.6),
            ], fallback: .success)
    }

    private func transient(at time: TimeInterval, intensity: Float, sharpness: Float) -> CHHapticEvent {
        CHHapticEvent(
            eventType: .hapticTransient,
            parameters: [
                CHHapticEventParameter(parameterID: .hapticIntensity, value: intensity),
                CHHapticEventParameter(parameterID: .hapticSharpness, value: sharpness),
            ], relativeTime: time)
    }

    private func play(
        _ events: [CHHapticEvent], curves: [CHHapticParameterCurve] = [],
        fallback: UINotificationFeedbackGenerator.FeedbackType
    ) {
        guard CHHapticEngine.capabilitiesForHardware().supportsHaptics else {
            UINotificationFeedbackGenerator().notificationOccurred(fallback)
            return
        }
        do {
            if engine == nil {
                let engine = try CHHapticEngine()
                engine.isAutoShutdownEnabled = true
                engine.resetHandler = { [weak self] in
                    Task { @MainActor in self?.engine = nil }
                }
                self.engine = engine
            }
            try engine?.start()
            let pattern = try CHHapticPattern(events: events, parameterCurves: curves)
            try engine?.makePlayer(with: pattern).start(atTime: CHHapticTimeImmediate)
        } catch {
            UINotificationFeedbackGenerator().notificationOccurred(fallback)
        }
    }
}
