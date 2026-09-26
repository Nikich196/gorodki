import AVFoundation
import CoreLocation
import CoreMotion
import Foundation
import Observation
import UIKit

// Системная часть экранов забега: разрешения перед «Стартом», голос и вибрация, компас. Правил здесь нет — модель
// экрана (`RunScreenModel`) решает, что и когда, здесь — только вызовы iOS.

/// Разрешения «Старта» у системы: геопозиция «При использовании» и «Движение и фитнес».
@MainActor
final class SystemRunStartAccess: RunStartAccess {
    func needsPrimer(_ permission: RunStartPermission) -> Bool {
        switch permission {
        case .location:
            CLLocationManager().authorizationStatus == .notDetermined
        case .motion:
            CMMotionActivityManager.isActivityAvailable()
                && CMMotionActivityManager.authorizationStatus() == .notDetermined
        }
    }

    var locationDenied: Bool {
        switch CLLocationManager().authorizationStatus {
        case .denied, .restricted: true
        default: false
        }
    }

    func request(_ permission: RunStartPermission) async {
        switch permission {
        case .location: _ = await RunController.shared.requestLocationAuthorization()
        case .motion: _ = await RunController.motionAuthorization()
        }
    }
}

/// Голос (`AVSpeechSynthesizer` по-русски), вибрация (`UIImpactFeedbackGenerator`, `UINotificationFeedbackGenerator`)
/// и оповещение Live Activity. Голос — поверх музыки с приглушением (PLAN.md, §6.9: `.playback`, `.voicePrompt`,
/// `.duckOthers`); в кармане его держит фоновый режим `audio` (Info.plist, PLAN.md, §7.2).
@MainActor
final class SystemRunFeedback: NSObject, RunFeedback, AVSpeechSynthesizerDelegate {
    private let synthesizer = AVSpeechSynthesizer()
    private let voice = AVSpeechSynthesisVoice(language: "ru-RU")

    override init() {
        super.init()
        synthesizer.delegate = self
    }

    func haptic(_ event: RunFeedbackEvent) {
        switch event {
        case .closure(let threshold):
            // 50 м — «ближе» (.increase), 15 м — «почти» (.levelChange): два разных удара (§6.9).
            UIImpactFeedbackGenerator(style: threshold > 20 ? .light : .heavy).impactOccurred()
        case .loopClosed:
            UIImpactFeedbackGenerator(style: .rigid).impactOccurred(intensity: 1)
        case .decided(let applied):
            UINotificationFeedbackGenerator().notificationOccurred(applied ? .success : .error)
        case .warning:
            UINotificationFeedbackGenerator().notificationOccurred(.warning)
        }
    }

    func speak(_ text: String) {
        guard !text.isEmpty else { return }
        let session = AVAudioSession.sharedInstance()
        try? session.setCategory(.playback, mode: .voicePrompt, options: [.duckOthers])
        try? session.setActive(true)
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = voice
        synthesizer.speak(utterance)
    }

    func alert(title: String, body: String) {
        RunController.shared.alertLiveActivity(title: title, body: body)
    }

    /// Договорили — вернуть громкость музыке.
    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        Task { @MainActor in
            guard !self.synthesizer.isSpeaking else { return }
            try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        }
    }
}

/// Компас для стрелки «до замыкания» (`CLHeading`, решение 25.09, пункт 2): куда смотрит верх телефона, градусы от
/// севера. Работает, пока открыт HUD; нет компаса — `heading` остаётся `nil`, и стрелка идёт по курсу движения. Один на
/// приложение: вид HUD пересоздаётся часто, а менеджер геопозиции создавать каждый раз незачем.
@MainActor
@Observable
final class HeadingSource: NSObject, CLLocationManagerDelegate {
    static let shared = HeadingSource()

    private(set) var heading: Double?
    @ObservationIgnored private let manager = CLLocationManager()

    override init() {
        super.init()
        manager.delegate = self
        manager.headingFilter = 5
    }

    func start() {
        guard CLLocationManager.headingAvailable() else { return }
        manager.startUpdatingHeading()
    }

    func stop() {
        manager.stopUpdatingHeading()
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateHeading newHeading: CLHeading) {
        // Точный север — если есть геопозиция; иначе магнитный. Отрицательная точность — показание негодное.
        let value = newHeading.trueHeading >= 0 ? newHeading.trueHeading : newHeading.magneticHeading
        let valid = newHeading.headingAccuracy >= 0
        Task { @MainActor in self.heading = valid ? value : nil }
    }
}

extension RunScreenModel {
    /// Экраны забега приложения: забег — `RunController`, разрешения, голос и вибрация — системные. Снимки трекера
    /// и отчёты синхронизации приходят сюда (`RunController.onState`, `onReport`).
    static func live(profile: ProfileModel) -> RunScreenModel {
        let controller = RunController.shared
        let model = RunScreenModel(
            profile: profile, driver: controller, access: SystemRunStartAccess(), feedback: SystemRunFeedback(),
            makeResult: { RunResultModel.live(runId: $0, justFinished: true) },
            queueSurvivesRestart: AppDependencies.shared.queueSurvivesRestart)
        model.connect = { [weak model] in
            controller.onState = { [weak model] state in model?.receive(state) }
            controller.onReport = { [weak model] report in model?.receive(report) }
            model?.receive(controller.state)
        }
        return model
    }
}
