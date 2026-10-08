import DesignSystem
import GameCore
import Persistence
import SwiftUI
import Synchronization
import UIKit

/// След для видео-повтора и откуда он.
struct ReplayTrack: Equatable, Sendable {
    var title: String
    var detail: String
    var coordinates: [Coordinate]
    /// Образец, а не забег игрока: своих забегов на телефоне ещё нет.
    var isSample: Bool

    /// Центр Бреста — вокруг него образец петли.
    static let brest = Coordinate(latitude: 52.0976, longitude: 23.7341)

    static var sample: ReplayTrack {
        ReplayTrack(
            title: "Образец", detail: "Своих забегов на телефоне пока нет — петля-образец около 1,5 км",
            coordinates: TrailReplay.sampleLoop(around: brest), isSample: true)
    }

    /// Последний забег из истории на телефоне; нет — образец.
    static func latest() async -> ReplayTrack {
        guard let history = AppDependencies.shared.history,
            let entry = try? await history.entries().first,
            let points = try? await history.points(of: entry.id), points.count >= 2
        else { return .sample }
        let date = Date(unix: Double(entry.startedAtMs) / 1_000).formatted(
            .dateTime.day().month(.wide).hour().minute().locale(Locale(identifier: "ru_RU")))
        return ReplayTrack(
            title: "Последний забег", detail: "\(date) · точек: \(points.count)",
            coordinates: points.map(\.coordinate), isSample: false)
    }
}

/// «Видео-повтор забега» (пункты 8 и 9 листика): ролик MP4 из кадров следа (`TrailReplay` → `ReplayVideoWriter`)
/// собирается фоновой задачей `BGContinuedProcessingTask` с системным прогрессом и играет в «картинке в картинке».
/// Готовый ролик и кадр итога — в «Фото».
@MainActor
@Observable
final class ReplayModel {
    enum Phase: Equatable {
        case idle
        case building(progress: Double, mode: ReplayBuildMode?)
        case ready(URL)
        case failed(String)
    }

    var track: ReplayTrack?
    var phase = Phase.idle
    var message: String?
    /// Обложка — последний кадр ролика (весь след и километры).
    private(set) var poster: CGImage?
    private(set) var playback: ReplayPlayback?

    @ObservationIgnored let player: PlayerColor
    @ObservationIgnored let builder: any ReplayBuilding
    @ObservationIgnored let saver: any PhotoSaving
    @ObservationIgnored let loadTrack: @MainActor () async -> ReplayTrack

    init(
        player: PlayerColor, builder: any ReplayBuilding, saver: any PhotoSaving,
        track: @escaping @MainActor () async -> ReplayTrack
    ) {
        self.player = player
        self.builder = builder
        self.saver = saver
        loadTrack = track
    }

    static func live(player: PlayerColor = .blue) -> ReplayModel {
        ReplayModel(
            player: player, builder: ContinuedReplayBuilder(), saver: PhotoLibrarySaver(),
            track: { await ReplayTrack.latest() })
    }

    /// След из чужого GPX («Файлы и экспорт» → «Открыть GPX»).
    static func imported(_ imported: ImportedTrack, player: PlayerColor = .blue) -> ReplayModel {
        let track = ReplayTrack(
            title: imported.name, detail: "GPX из «Файлов» · точек: \(imported.coordinates.count)",
            coordinates: imported.coordinates, isSample: false)
        return ReplayModel(
            player: player, builder: ContinuedReplayBuilder(), saver: PhotoLibrarySaver(), track: { track })
    }

    var style: ReplayStyle {
        ReplayStyle(player: player, title: track?.isSample == true ? "образец" : "повтор забега")
    }

    func load() async {
        guard track == nil else { return }
        let track = await loadTrack()
        self.track = track
        let replay = TrailReplay(
            coordinates: track.coordinates, width: Double(ReplayVideoWriter.side),
            height: Double(ReplayVideoWriter.side), padding: 110, frameCount: 2)
        poster = ReplayRenderer.image(
            replay.frame(1), style: style, width: ReplayVideoWriter.side, height: ReplayVideoWriter.side)
    }

    var isBuilding: Bool {
        if case .building = phase { return true }
        return false
    }

    func build() async {
        guard let track, !isBuilding else { return }
        playback?.pause()
        playback = nil
        phase = .building(progress: 0, mode: nil)
        let url = ReplayStore.folder.appendingPathComponent("replay-\(Int(Date.now.timeIntervalSince1970)).mp4")
        let progress = ProgressRelay(model: self)
        do {
            try await builder.build(
                coordinates: track.coordinates, style: style, to: url,
                mode: { [weak self] mode in self?.setMode(mode) },
                progress: { fraction in progress.report(fraction) })
            let playback = ReplayPlayback(url: url)
            self.playback = playback
            phase = .ready(url)
            playback.play()
        } catch is CancellationError {
            phase = .failed("Сборку остановили — нажми «Собрать видео» ещё раз.")
        } catch {
            phase = .failed("Видео не собралось: \(error.localizedDescription)")
        }
    }

    fileprivate func setProgress(_ fraction: Double) {
        guard case .building(let old, let mode) = phase, fraction > old else { return }
        phase = .building(progress: fraction, mode: mode)
    }

    private func setMode(_ mode: ReplayBuildMode) {
        guard case .building(let progress, _) = phase else { return }
        phase = .building(progress: progress, mode: mode)
    }

    func saveVideo() async {
        guard case .ready(let url) = phase else { return }
        message = await saver.saveVideo(url).text(saved: "Видео — в «Фото».")
    }

    /// Снимок итога забега — последний кадр ролика — в «Фото».
    func savePoster() async {
        guard let poster, let data = UIImage(cgImage: poster).pngData() else { return }
        message = await saver.saveImage(data).text(saved: "Кадр итога — в «Фото».")
    }
}

/// Прогресс записи приходит с потока записи — на экран не чаще чем каждый процент.
private final class ProgressRelay: Sendable {
    private let model: ReplayModel
    private let shown = Mutex(-1)

    init(model: ReplayModel) {
        self.model = model
    }

    func report(_ fraction: Double) {
        let percent = Int(fraction * 100)
        let changed = shown.withLock { shown in
            defer { shown = max(shown, percent) }
            return percent > shown
        }
        guard changed else { return }
        Task { @MainActor [model] in model.setProgress(fraction) }
    }
}

struct ReplayView: View {
    @State private var model: ReplayModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(model: ReplayModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                media
                if let track = model.track {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(track.title)
                            .font(.title3.bold())
                            .fontDesign(.rounded)
                            .foregroundStyle(Palette.uiInk.color)
                        Text(track.detail)
                            .font(.subheadline)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    .padding(.horizontal, 4)
                }
                status
                actions
            }
            .padding(20)
        }
        .background(Palette.uiBackground.color)
        .navigationTitle("Видео-повтор")
        .task { await model.load() }
        .onDisappear { model.playback?.pause() }
    }

    private var media: some View {
        ZStack {
            Palette.mapLand.night.color
            if let playback = model.playback {
                ReplayPlayerView(playback: playback)
                    .transition(.opacity)
            } else if let poster = model.poster {
                Image(decorative: poster, scale: 1)
                    .resizable()
                    .scaledToFit()
                    .transition(.opacity)
            }
            if let playback = model.playback, playback.pipActive {
                Label("Играет в окне поверх приложений", systemImage: "pip")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.white)
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .clipShape(.rect(cornerRadius: Radius.card))
        .animation(Motion.numericAppear.unlessReduceMotion(reduceMotion), value: model.playback != nil)
        .accessibilityLabel("След забега")
    }

    @ViewBuilder
    private var status: some View {
        switch model.phase {
        case .idle:
            Text("Ролик на 7 секунд: след рисуется со свечением твоего цвета. Сборка идёт фоновой задачей iOS.")
                .font(.footnote)
                .foregroundStyle(Palette.uiInk2.color)
        case .building(let progress, let mode):
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Собираю кадры…")
                    Spacer()
                    Text(NumberText.integer(Int((progress * 100).rounded())) + NumberText.unitSeparator + "%")
                        .monospacedDigit()
                        .contentTransition(.numericText(value: progress))
                }
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
                ProgressView(value: progress)
                    .tint(model.player.edgeColor)
                    .animation(.linear(duration: 0.2), value: progress)
                Text(mode.map(Self.modeText) ?? "Прошу у iOS фоновую задачу…")
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            .contentCard()
        case .ready:
            Text("Готово. «Картинка в картинке» — окно останется поверх Карт и других приложений.")
                .font(.footnote)
                .foregroundStyle(Palette.uiInk2.color)
        case .failed(let text):
            Text(text)
                .font(.footnote)
                .foregroundStyle(Palette.uiInk2.color)
        }
    }

    private static func modeText(_ mode: ReplayBuildMode) -> String {
        switch mode {
        case .continuedProcessing:
            "Фоновая задача iOS: прогресс — в системной плашке, приложение можно свернуть."
        case .foreground(let reason):
            "Собираю здесь, на экране: \(reason)."
        }
    }

    private var actions: some View {
        VStack(spacing: 12) {
            Button {
                Task { await model.build() }
            } label: {
                Label(
                    model.playback == nil ? "Собрать видео" : "Собрать заново",
                    systemImage: "film.stack")
            }
            .buttonStyle(.neutral)
            .disabled(model.isBuilding || model.track == nil)
            if let playback = model.playback {
                Button {
                    playback.togglePictureInPicture()
                } label: {
                    Label(
                        playback.pipActive ? "Вернуть из окна" : "Картинка в картинке",
                        systemImage: playback.pipActive ? "pip.exit" : "pip.enter")
                }
                .buttonStyle(.neutral)
                if let text = playback.message {
                    Text(text)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            HStack(spacing: 12) {
                if case .ready(let url) = model.phase {
                    secondary("Видео в «Фото»", systemImage: "square.and.arrow.down") {
                        Task { await model.saveVideo() }
                    }
                    ShareLink(item: url) {
                        Label("Отправить", systemImage: "square.and.arrow.up")
                            .frame(maxWidth: .infinity, minHeight: 44)
                    }
                    .foregroundStyle(Palette.uiInk.color)
                    .background(Palette.uiCell.color, in: .capsule)
                }
                secondary("Кадр в «Фото»", systemImage: "photo.badge.plus") {
                    Task { await model.savePoster() }
                }
                .disabled(model.poster == nil)
            }
            .font(.subheadline.weight(.semibold))
            if let message = model.message {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        }
    }

    private func secondary(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .frame(maxWidth: .infinity, minHeight: 44)
        }
        .foregroundStyle(Palette.uiInk.color)
        .background(Palette.uiCell.color, in: .capsule)
    }
}
