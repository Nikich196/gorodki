@preconcurrency import AVFoundation
import SwiftUI
import UIKit

/// Разрешение на камеру, как его видит экран.
enum CameraAccess: Equatable, Sendable {
    case notDetermined, denied, allowed
}

/// Своя камера (пункт 13 листика): сессия AVFoundation, чтение QR (`AVCaptureMetadataOutput`) и снимок
/// (`AVCapturePhotoOutput`). За протоколом: в режиме фикстур и на симуляторе без камеры экран сканера показывается
/// без неё.
@MainActor
protocol CameraControlling: AnyObject {
    /// Сессия для превью; `nil` — камеры нет (фикстура, симулятор).
    var session: AVCaptureSession? { get }
    func access() -> CameraAccess
    func requestAccess() async -> CameraAccess
    /// Запустить камеру; `onCode` — прочитанный QR. `false` — камеры на устройстве нет.
    func start(onCode: @escaping @MainActor (String) -> Void) -> Bool
    func stop()
    /// Снимок JPEG/HEIC; `nil` — не вышло.
    func capturePhoto() async -> Data?
}

/// Сессия живёт на своей очереди: `startRunning` блокирует поток, его нельзя звать с главного.
private final class SessionQueue: @unchecked Sendable {
    let session = AVCaptureSession()
    let queue = DispatchQueue(label: "gorodki.camera")
}

@MainActor
final class CameraController: NSObject, CameraControlling {
    private let box = SessionQueue()
    private let metadata = AVCaptureMetadataOutput()
    private let photo = AVCapturePhotoOutput()
    private var configured = false
    private var onCode: (@MainActor (String) -> Void)?
    private var photoContinuation: CheckedContinuation<Data?, Never>?

    var session: AVCaptureSession? { configured ? box.session : nil }

    func access() -> CameraAccess {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined: .notDetermined
        case .authorized: .allowed
        default: .denied
        }
    }

    func requestAccess() async -> CameraAccess {
        _ = await AVCaptureDevice.requestAccess(for: .video)
        return access()
    }

    func start(onCode: @escaping @MainActor (String) -> Void) -> Bool {
        self.onCode = onCode
        if !configured {
            guard configure() else { return false }
        }
        let box = self.box
        box.queue.async {
            if !box.session.isRunning { box.session.startRunning() }
        }
        return true
    }

    func stop() {
        let box = self.box
        box.queue.async {
            if box.session.isRunning { box.session.stopRunning() }
        }
    }

    func capturePhoto() async -> Data? {
        guard configured, photoContinuation == nil else { return nil }
        return await withCheckedContinuation { continuation in
            photoContinuation = continuation
            photo.capturePhoto(with: AVCapturePhotoSettings(), delegate: self)
        }
    }

    /// Задняя камера, выход QR и выход снимков. Камеры нет (симулятор) — `false`.
    private func configure() -> Bool {
        let session = box.session
        guard let camera = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back),
            let input = try? AVCaptureDeviceInput(device: camera)
        else { return false }
        session.beginConfiguration()
        session.sessionPreset = .photo
        guard session.canAddInput(input), session.canAddOutput(metadata), session.canAddOutput(photo) else {
            session.commitConfiguration()
            return false
        }
        session.addInput(input)
        session.addOutput(metadata)
        session.addOutput(photo)
        metadata.setMetadataObjectsDelegate(self, queue: .main)
        metadata.metadataObjectTypes = [.qr]
        session.commitConfiguration()
        configured = true
        return true
    }

    fileprivate func finishPhoto(_ data: Data?) {
        photoContinuation?.resume(returning: data)
        photoContinuation = nil
    }

    fileprivate func found(_ code: String) {
        onCode?(code)
    }
}

extension CameraController: AVCaptureMetadataOutputObjectsDelegate {
    /// Очередь делегата — главная (`setMetadataObjectsDelegate(_:queue: .main)`).
    nonisolated func metadataOutput(
        _ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        let code = metadataObjects.compactMap { ($0 as? AVMetadataMachineReadableCodeObject)?.stringValue }.first
        guard let code else { return }
        MainActor.assumeIsolated { found(code) }
    }
}

extension CameraController: AVCapturePhotoCaptureDelegate {
    nonisolated func photoOutput(
        _ output: AVCapturePhotoOutput, didFinishProcessingPhoto photo: AVCapturePhoto, error: (any Error)?
    ) {
        let data = error == nil ? photo.fileDataRepresentation() : nil
        Task { @MainActor in self.finishPhoto(data) }
    }
}

/// Превью камеры — слой `AVCaptureVideoPreviewLayer` на весь экран.
struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspectFill
        return view
    }

    func updateUIView(_ view: PreviewView, context: Context) {}

    final class PreviewView: UIView {
        override static var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }

        var previewLayer: AVCaptureVideoPreviewLayer {
            // Слой этого вида — всегда превью: `layerClass` выше.
            layer as? AVCaptureVideoPreviewLayer ?? AVCaptureVideoPreviewLayer()
        }
    }
}
