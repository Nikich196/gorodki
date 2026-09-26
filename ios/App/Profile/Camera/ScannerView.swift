@preconcurrency import AVFoundation
import DesignSystem
import GameCore
import SwiftUI

/// Что прочитал сканер.
enum ScanResult: Equatable, Sendable {
    /// Код игры: друг или приглашение.
    case game(FriendLink)
    /// Чужой QR — показать как есть.
    case text(String)

    init(_ code: String) {
        self = FriendLink.parse(code).map(ScanResult.game) ?? .text(code)
    }
}

/// Сканер QR своей камерой (пункт 13 листика): разрешение — при первом открытии (PLAN.md, §6.6), код друга или
/// приглашения, снимок в «Фото».
@MainActor
@Observable
final class ScannerModel {
    enum State: Equatable {
        case idle, denied, unavailable, running
    }

    var state = State.idle
    var result: ScanResult?
    /// Сколько кодов прочитано — повод для хаптики и «захвата» рамки.
    var scans = 0
    var message: String?

    @ObservationIgnored let camera: any CameraControlling
    @ObservationIgnored let saver: any PhotoSaving

    init(camera: any CameraControlling, saver: any PhotoSaving) {
        self.camera = camera
        self.saver = saver
    }

    static func live() -> ScannerModel { ScannerModel(camera: CameraController(), saver: PhotoLibrarySaver()) }

    func start() async {
        var access = camera.access()
        if access == .notDetermined {
            access = await camera.requestAccess()
        }
        guard access == .allowed else {
            state = .denied
            return
        }
        state = camera.start { [weak self] code in self?.found(code) } ? .running : .unavailable
    }

    func stop() {
        camera.stop()
    }

    func found(_ code: String) {
        let result = ScanResult(code)
        guard result != self.result else { return }
        self.result = result
        scans += 1
    }

    /// Снимок своей камерой — в «Фото» (доступ только на добавление).
    func takePhoto() async {
        guard let data = await camera.capturePhoto() else {
            message = "Снимок не получился."
            return
        }
        message = await saver.saveImage(data).text(saved: "Снимок — в «Фото».")
    }
}

struct ScannerView: View {
    @State private var model: ScannerModel
    @State private var framed = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.openURL) private var openURL

    init(model: ScannerModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let session = model.camera.session, model.state == .running {
                CameraPreview(session: session)
                    .ignoresSafeArea()
            }
            viewfinder
            VStack {
                Spacer()
                bottomPanel
            }
            .padding(Metrics.panelInset)
        }
        .navigationTitle("Сканер")
        .navigationBarTitleDisplayMode(.inline)
        .toolbarColorScheme(.dark, for: .navigationBar)
        .sensoryFeedback(.success, trigger: model.scans)
        .task {
            await model.start()
            withAnimation(Animation.spring(Motion.captureEstimate).unlessReduceMotion(reduceMotion)) { framed = true }
        }
        .onDisappear { model.stop() }
    }

    /// Рамка видоискателя: уголки въезжают при открытии и «схватывают» код — сжимаются, когда он прочитан.
    private var viewfinder: some View {
        let locked = model.result != nil
        return ViewfinderCorners()
            .stroke(.white, style: StrokeStyle(lineWidth: 5, lineCap: .round, lineJoin: .round))
            .frame(width: 250, height: 250)
            .scaleEffect(framed ? (locked ? 0.9 : 1) : 1.25)
            .opacity(framed ? 1 : 0)
            .shadow(color: .black.opacity(0.35), radius: 8)
            .animation(Animation.spring(Motion.captureEstimate).unlessReduceMotion(reduceMotion), value: locked)
            .accessibilityHidden(true)
    }

    @ViewBuilder
    private var bottomPanel: some View {
        VStack(spacing: 14) {
            switch model.state {
            case .idle:
                Text("Открываю камеру…")
            case .denied:
                Text("Нет доступа к камере. Разрешить его можно в Настройках.")
                    .multilineTextAlignment(.center)
                Button("Открыть Настройки") {
                    if let url = SystemSettings.appURL { openURL(url) }
                }
                .buttonStyle(.glass)
            case .unavailable:
                Text("Камеры на этом устройстве нет — сканер работает на iPhone.")
                    .multilineTextAlignment(.center)
            case .running:
                resultText
                HStack(spacing: 16) {
                    Button {
                        Task { await model.takePhoto() }
                    } label: {
                        Label("Снимок в «Фото»", systemImage: "camera")
                            .labelStyle(.iconOnly)
                            .font(.title2.weight(.semibold))
                            .frame(width: Metrics.controlHeight, height: Metrics.controlHeight)
                    }
                    .buttonStyle(.plain)
                    .glassEffect(.regular.interactive(), in: .circle)
                }
            }
            if let message = model.message {
                Text(message)
                    .font(.footnote)
            }
        }
        .font(.body)
        .foregroundStyle(.primary)
        .padding(20)
        .frame(maxWidth: .infinity)
        .panelGlass()
        .environment(\.colorScheme, .dark)
    }

    @ViewBuilder
    private var resultText: some View {
        switch model.result {
        case nil:
            Text("Наведи камеру на QR друга или код приглашения")
                .multilineTextAlignment(.center)
        case .game(.player(_, let name)):
            Label(
                "Игрок \(name ?? "без ника") — друзья появятся с кланами",
                systemImage: "person.crop.circle.badge.checkmark")
        case .game(.invite(let code)):
            Label("Код приглашения \(code) — впиши его при регистрации", systemImage: "ticket")
        case .text(let text):
            Label(text, systemImage: "qrcode")
                .lineLimit(2)
        }
    }
}

/// Четыре уголка рамки видоискателя.
struct ViewfinderCorners: Shape {
    func path(in rect: CGRect) -> Path {
        let arm = min(rect.width, rect.height) * 0.18
        var path = Path()
        for (corner, dx, dy) in [
            (CGPoint(x: rect.minX, y: rect.minY), 1.0, 1.0), (CGPoint(x: rect.maxX, y: rect.minY), -1.0, 1.0),
            (CGPoint(x: rect.minX, y: rect.maxY), 1.0, -1.0), (CGPoint(x: rect.maxX, y: rect.maxY), -1.0, -1.0),
        ] {
            path.move(to: CGPoint(x: corner.x, y: corner.y + dy * arm))
            path.addLine(to: corner)
            path.addLine(to: CGPoint(x: corner.x + dx * arm, y: corner.y))
        }
        return path
    }
}
