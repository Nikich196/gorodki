import AVFoundation
import Photos
import PhotosUI
import Synchronization
import UIKit

/// Разрешение на медиатеку, как его видит «Галерея» (пункт 3 листика).
enum PhotoAccess: Equatable, Sendable {
    case notDetermined, denied
    /// Ограниченный доступ: игрок выбрал несколько фото — видны только они, «Выбрать ещё» расширяет выбор.
    case limited
    case full

    var canRead: Bool { self == .limited || self == .full }

    init(_ status: PHAuthorizationStatus) {
        switch status {
        case .notDetermined: self = .notDetermined
        case .limited: self = .limited
        case .authorized: self = .full
        default: self = .denied
        }
    }
}

/// Фильтр «Галереи».
enum GalleryFilter: String, CaseIterable, Identifiable, Sendable {
    case all, geotagged, videos

    var id: String { rawValue }

    var title: String {
        switch self {
        case .all: "Все"
        case .geotagged: "С геотегом"
        case .videos: "Видео"
        }
    }
}

/// Фото или видео медиатеки — простыми значениями.
struct GalleryItem: Identifiable, Hashable, Sendable {
    /// `PHAsset.localIdentifier`.
    var id: String
    var isVideo: Bool
    /// Длина видео, с.
    var duration: Double
    var createdAt: Date?
    /// Где снято: широта и долгота; `nil` — без геотега.
    var latitude: Double?
    var longitude: Double?

    var hasLocation: Bool { latitude != nil && longitude != nil }
}

/// Итог сохранения в «Фото».
enum SaveResult: Equatable, Sendable {
    case saved
    case denied
    case failed(String)

    /// Текст для игрока: `saved` — что сказать при успехе.
    func text(saved: String) -> String {
        switch self {
        case .saved: saved
        case .denied: "Нет доступа к «Фото» на добавление — его можно дать в Настройках."
        case .failed(let error): "Не сохранилось: \(error)"
        }
    }
}

/// Сохранение в «Фото» — доступ только на добавление (`.addOnly`): игра кладёт снимки и видео, но не читает медиатеку.
@MainActor
protocol PhotoSaving {
    func saveImage(_ data: Data) async -> SaveResult
    func saveVideo(_ url: URL) async -> SaveResult
}

struct PhotoLibrarySaver: PhotoSaving {
    func saveImage(_ data: Data) async -> SaveResult {
        await Self.save { PHAssetCreationRequest.forAsset().addResource(with: .photo, data: data, options: nil) }
    }

    func saveVideo(_ url: URL) async -> SaveResult {
        await Self.save { _ = PHAssetChangeRequest.creationRequestForAssetFromVideo(atFileURL: url) }
    }

    @concurrent
    nonisolated
        private static func save(_ change: @escaping @Sendable () -> Void) async -> SaveResult
    {
        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else { return .denied }
        do {
            try await PHPhotoLibrary.shared().performChanges(change)
            return .saved
        } catch {
            return .failed(error.localizedDescription)
        }
    }
}

/// Медиатека для «Галереи»: список по фильтру и миниатюры с кэшем. За протоколом: режим фикстур показывает сетку
/// из нарисованных плиток без системного запроса.
@MainActor
protocol PhotoLibraryProviding: AnyObject {
    func access() -> PhotoAccess
    func requestAccess() async -> PhotoAccess
    func items(_ filter: GalleryFilter) async -> [GalleryItem]
    /// Миниатюра размера `size` (пиксели); кэш — `PHCachingImageManager`.
    func thumbnail(for item: GalleryItem, size: CGSize) async -> UIImage?
    /// Готовить миниатюры заранее (`PreheatWindow`) и отпускать ушедшие.
    func startCaching(_ items: [GalleryItem], size: CGSize)
    func stopCaching(_ items: [GalleryItem], size: CGSize)
    /// Видео для просмотра; `nil` — не загрузилось (в том числе из iCloud без сети).
    func playerItem(for item: GalleryItem) async -> AVPlayerItemBox?
    /// Ограниченный доступ: системное окно «Выбрать ещё фото».
    func manageLimitedSelection() async
}

/// Продолжение возобновляется ровно один раз, с какого бы потока ни пришёл ответ.
final class OnceFlag: Sendable {
    private let done = Mutex(false)

    /// `true` — первый раз.
    func claim() -> Bool {
        done.withLock { done in
            defer { done = true }
            return !done
        }
    }
}

/// `UIImage` из ответа PhotoKit — через границу потока.
final class ImageBox: @unchecked Sendable {
    let image: UIImage

    init(_ image: UIImage) {
        self.image = image
    }
}

/// `AVPlayerItem` не `Sendable` — несёт его через границу актора коробка (создан и дальше используется на главном).
final class AVPlayerItemBox: @unchecked Sendable {
    let item: AVPlayerItem

    init(_ item: AVPlayerItem) {
        self.item = item
    }
}

/// `PHPhotoLibrary` и `PHCachingImageManager`.
@MainActor
final class SystemPhotoLibrary: PhotoLibraryProviding {
    private let manager = PHCachingImageManager()
    /// Найденные ассеты по идентификатору — чтобы миниатюры не искали ассет заново.
    private var assets: [String: PHAsset] = [:]

    func access() -> PhotoAccess {
        PhotoAccess(PHPhotoLibrary.authorizationStatus(for: .readWrite))
    }

    func requestAccess() async -> PhotoAccess {
        PhotoAccess(await PHPhotoLibrary.requestAuthorization(for: .readWrite))
    }

    func items(_ filter: GalleryFilter) async -> [GalleryItem] {
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        if filter == .videos {
            options.predicate = NSPredicate(format: "mediaType == %d", PHAssetMediaType.video.rawValue)
        } else {
            options.predicate = NSPredicate(
                format: "mediaType == %d OR mediaType == %d", PHAssetMediaType.image.rawValue,
                PHAssetMediaType.video.rawValue)
        }
        // Последние 5 000 — сетке больше не нужно, а обход всей медиатеки на десятки тысяч фото заметен.
        options.fetchLimit = 5_000
        let result = PHAsset.fetchAssets(with: options)
        var found: [GalleryItem] = []
        found.reserveCapacity(result.count)
        for index in 0..<result.count {
            let asset = result.object(at: index)
            // Геотег — у ассета, фильтра по нему в PHFetchOptions нет.
            if filter == .geotagged && asset.location == nil { continue }
            assets[asset.localIdentifier] = asset
            found.append(
                GalleryItem(
                    id: asset.localIdentifier, isVideo: asset.mediaType == .video, duration: asset.duration,
                    createdAt: asset.creationDate, latitude: asset.location?.coordinate.latitude,
                    longitude: asset.location?.coordinate.longitude))
        }
        return found
    }

    func thumbnail(for item: GalleryItem, size: CGSize) async -> UIImage? {
        guard let asset = assets[item.id] else { return nil }
        let options = PHImageRequestOptions()
        options.deliveryMode = .opportunistic
        options.isNetworkAccessAllowed = true
        options.resizeMode = .fast
        let resumed = OnceFlag()
        return await withCheckedContinuation { continuation in
            manager.requestImage(for: asset, targetSize: size, contentMode: .aspectFill, options: options) {
                image, info in
                // Сначала может прийти черновик низкого качества — ждём окончательную картинку (или ошибку).
                let degraded = (info?[PHImageResultIsDegradedKey] as? Bool) ?? false
                guard !degraded || image == nil, resumed.claim() else { return }
                continuation.resume(returning: image.map(ImageBox.init))
            }
        }?.image
    }

    func startCaching(_ items: [GalleryItem], size: CGSize) {
        let found = items.compactMap { assets[$0.id] }
        manager.startCachingImages(for: found, targetSize: size, contentMode: .aspectFill, options: nil)
    }

    func stopCaching(_ items: [GalleryItem], size: CGSize) {
        let found = items.compactMap { assets[$0.id] }
        manager.stopCachingImages(for: found, targetSize: size, contentMode: .aspectFill, options: nil)
    }

    func playerItem(for item: GalleryItem) async -> AVPlayerItemBox? {
        guard let asset = assets[item.id] else { return nil }
        let options = PHVideoRequestOptions()
        options.isNetworkAccessAllowed = true
        return await withCheckedContinuation { continuation in
            PHImageManager.default().requestPlayerItem(forVideo: asset, options: options) { item, _ in
                continuation.resume(returning: item.map(AVPlayerItemBox.init))
            }
        }
    }

    func manageLimitedSelection() async {
        guard let controller = TopViewController.current else { return }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            PHPhotoLibrary.shared().presentLimitedLibraryPicker(from: controller) { _ in
                continuation.resume()
            }
        }
    }
}
