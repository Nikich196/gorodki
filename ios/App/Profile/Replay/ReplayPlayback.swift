@preconcurrency import AVFoundation
import AVKit
import SwiftUI
import UIKit

/// Проигрыватель видео-повтора с «картинкой в картинке» (пункт 9 листика): ролик идёт по кругу (`AVPlayerLooper`),
/// окно PiP — поверх любых приложений, например Карт. Слой — `AVPlayerLayer`, контроллер —
/// `AVPictureInPictureController` (базовый PiP, без своих кнопок). Нужен фоновый режим `audio` (Info.plist): без него
/// iOS не оставит окно при сворачивании приложения.
@MainActor
@Observable
final class ReplayPlayback: NSObject {
    private(set) var pipActive = false
    private(set) var message: String?

    let player: AVQueuePlayer
    let view = PlayerLayerView()
    @ObservationIgnored private var looper: AVPlayerLooper?
    @ObservationIgnored private var pip: AVPictureInPictureController?

    init(url: URL) {
        player = AVQueuePlayer()
        super.init()
        let item = AVPlayerItem(url: url)
        looper = AVPlayerLooper(player: player, templateItem: item)
        player.isMuted = true
        view.playerLayer.player = player
        view.playerLayer.videoGravity = .resizeAspect
        if AVPictureInPictureController.isPictureInPictureSupported() {
            pip = AVPictureInPictureController(playerLayer: view.playerLayer)
            pip?.canStartPictureInPictureAutomaticallyFromInline = true
            pip?.delegate = self
        }
    }

    func play() {
        // Категория «воспроизведение»: PiP работает только с ней, а беззвучный ролик не прервёт музыку игрока.
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback, options: [.mixWithOthers])
        try? AVAudioSession.sharedInstance().setActive(true)
        player.play()
    }

    func pause() {
        player.pause()
    }

    func togglePictureInPicture() {
        guard let pip else {
            message = "«Картинка в картинке» на этом устройстве недоступна."
            return
        }
        if pip.isPictureInPictureActive {
            pip.stopPictureInPicture()
        } else if pip.isPictureInPicturePossible {
            play()
            pip.startPictureInPicture()
            message = nil
        } else {
            message = "Окно ещё не готово — подожди секунду и нажми снова."
        }
    }

    fileprivate func setActive(_ active: Bool) {
        pipActive = active
    }
}

extension ReplayPlayback: AVPictureInPictureControllerDelegate {
    nonisolated func pictureInPictureControllerDidStartPictureInPicture(
        _ controller: AVPictureInPictureController
    ) {
        Task { @MainActor in self.setActive(true) }
    }

    nonisolated func pictureInPictureControllerDidStopPictureInPicture(_ controller: AVPictureInPictureController) {
        Task { @MainActor in self.setActive(false) }
    }
}

/// Вид со слоем `AVPlayerLayer` — его берёт контроллер PiP.
final class PlayerLayerView: UIView {
    override static var layerClass: AnyClass { AVPlayerLayer.self }

    var playerLayer: AVPlayerLayer {
        // Слой этого вида — всегда `AVPlayerLayer`: `layerClass` выше.
        layer as? AVPlayerLayer ?? AVPlayerLayer()
    }
}

/// Готовый вид проигрывателя в SwiftUI.
struct ReplayPlayerView: UIViewRepresentable {
    let playback: ReplayPlayback

    func makeUIView(context: Context) -> PlayerLayerView {
        playback.view.backgroundColor = .black
        return playback.view
    }

    func updateUIView(_ view: PlayerLayerView, context: Context) {}
}
