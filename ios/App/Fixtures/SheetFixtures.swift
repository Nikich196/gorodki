#if DEBUG
    import AVFoundation
    import DesignSystem
    import Foundation
    import GameCore
    import GorodkiAPI
    import Persistence
    import SwiftUI
    import Sync
    import UIKit

    /// Экраны пунктов листика в режиме фикстур (`-GorodkiScreen gallery` и др.): модели с простыми значениями
    /// и подменами платформы — без системных запросов разрешений, камеры, Календаря и медиатеки.
    struct SheetFixtureScreen: View {
        let screen: FixtureScreen

        var body: some View {
            NavigationStack {
                content
            }
        }

        @ViewBuilder
        private var content: some View {
            switch screen {
            case .gallery: GalleryView(model: SheetFixtures.gallery())
            case .replay: ReplayView(model: SheetFixtures.replay())
            case .storage: StorageView(model: SheetFixtures.storage())
            case .backup: BackupView(model: SheetFixtures.backup())
            case .files: FilesView(model: SheetFixtures.files())
            case .calendar: CalendarView(model: SheetFixtures.calendar())
            case .invite: InviteView(model: SheetFixtures.invite())
            case .myQR:
                MyQRView(
                    playerId: SheetFixtures.me?.id, playerName: SheetFixtures.me?.displayName,
                    player: SheetFixtures.player, inviteCode: "ABCD-2345")
            case .scanner: ScannerView(model: SheetFixtures.scanner())
            case .notifications: NotificationsView(model: SheetFixtures.notifications())
            default:
                AssignmentSheetView(
                    player: SheetFixtures.player, playerId: SheetFixtures.me?.id,
                    playerName: SheetFixtures.me?.displayName)
            }
        }
    }

    /// Данные экранов листика: образцы `contracts/samples` и числа, похожие на телефон после недели игры.
    @MainActor
    enum SheetFixtures {
        /// «Сейчас» фикстур — 25.11.2026, середина Сезона 0 (`seasons.json`): до конца — четыре дня.
        nonisolated static let now: Double = 1_795_600_000

        static var me: Components.Schemas.MeResponse? {
            Fixtures.sample("me", as: Components.Schemas.MeResponse.self)
        }

        static var player: PlayerColor {
            me.map { PlayerColor(index: Int($0.colorIndex)) } ?? .blue
        }

        static var seasons: [SeasonInfo] {
            Fixtures.sample("seasons", as: Components.Schemas.SeasonsResponse.self).map(SeasonsSource.seasons(from:))
                ?? []
        }

        /// Свои UserDefaults — фикстуры не трогают настройки приложения и не зависят от прошлого запуска.
        static func defaults() -> UserDefaults {
            let name = "gorodki.fixtures"
            let defaults = UserDefaults(suiteName: name) ?? .standard
            defaults.removePersistentDomain(forName: name)
            return defaults
        }

        static func gallery() -> GalleryModel {
            GalleryModel(library: FixturePhotoLibrary())
        }

        static func replay() -> ReplayModel {
            let model = ReplayModel(
                player: player, builder: FixtureReplayBuilder(), saver: FixturePhotoSaver(),
                track: {
                    ReplayTrack(
                        title: "Последний забег", detail: "25 ноября, 7:30 · точек: 412",
                        coordinates: TrailReplay.sampleLoop(around: ReplayTrack.brest), isSample: false)
                })
            model.phase = .building(progress: 0.62, mode: .continuedProcessing)
            return model
        }

        static func storage() -> StorageModel {
            let snapshot = StorageSnapshot(
                territory: FileCount(bytes: 3_412_000, files: 118), fog: FileCount(bytes: 1_184_000, files: 46),
                replays: FileCount(bytes: 12_650_000, files: 2), database: FileCount(bytes: 842_000, files: 1),
                exports: FileCount(bytes: 64_300, files: 3), unsentRuns: 1, historyRuns: 12)
            return StorageModel(source: StorageSource(measure: { snapshot }, clearCache: {}))
        }

        static func backup() -> BackupModel {
            let stats = Fixtures.sample("me-stats", as: Components.Schemas.MyStatsResponse.self)
            let summary = me.map { me in
                BackupServerSummary(
                    displayName: me.displayName, publicProfile: me.publicProfile, runs: Int(stats?.runs ?? 0),
                    distanceMeters: stats?.distanceMeters ?? 0, exploredSquareMeters: stats?.exploredSquareMeters ?? 0,
                    explorationRank: stats?.explorationRank.map(Int.init), privacyZones: 1)
            }
            let nowMs = Int64(Date.now.timeIntervalSince1970 * 1_000)
            let log = SyncLog.State(
                last: SyncPassRecord(atMs: nowMs - 4 * 60_000, outcome: .offline),
                lastSuccessAtMs: nowMs - 52 * 60_000)
            return BackupModel(
                source: BackupSource(
                    summary: { summary }, queue: { BackupQueue(unsentRuns: 1, unsettledClaims: 2) }, log: { log },
                    syncNow: { true }))
        }

        static func files() -> FilesModel {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Exports", isDirectory: true)
            let exports = [
                StoredFile(
                    url: folder.appendingPathComponent("gorodki-2026-11-25-0730-run.gpx"), bytes: 48_200,
                    modifiedAt: now - 3_600),
                StoredFile(
                    url: folder.appendingPathComponent("gorodki-my-data.json"), bytes: 12_900, modifiedAt: now - 86_400),
            ]
            let runs = [
                RunHistoryEntry(
                    id: UUID(), league: .run, source: .live, startedAtMs: Int64(now - 3_600) * 1_000,
                    endedAtMs: Int64(now - 1_500) * 1_000, pointCount: 412),
                RunHistoryEntry(
                    id: UUID(), league: .run, source: .live, startedAtMs: Int64(now - 90_000) * 1_000,
                    endedAtMs: Int64(now - 87_000) * 1_000, pointCount: 288),
            ]
            let store = defaults()
            store.set(Data([1]), forKey: AutoExport.bookmarkKey)
            store.set("Городки — забеги", forKey: AutoExport.folderNameKey)
            store.set(
                try? JSONEncoder().encode(
                    AutoExport.Record(fileName: "gorodki-2026-11-25-0730-run.gpx", atMs: Int64(now) * 1_000)),
                forKey: AutoExport.recordKey)
            return FilesModel(
                source: FilesSource(exports: { exports }, runs: { runs }, exportRun: { _ in nil }),
                autoExport: AutoExport(defaults: store))
        }

        static func calendar() -> CalendarModel {
            CalendarModel(
                calendar: FixtureCalendar(), seasons: { seasons }, defaults: defaults(), now: { now })
        }

        static func invite() -> InviteModel {
            let store = defaults()
            store.set("ABCD-2345", forKey: InviteModel.codeKey)
            let model = InviteModel(contacts: FixtureContacts(), defaults: store, senderName: me?.displayName)
            model.friend = FriendContact(name: "Аня Ковальчук", phone: "+375 29 123-45-67")
            return model
        }

        static func scanner() -> ScannerModel {
            let model = ScannerModel(camera: FixtureCamera(), saver: FixturePhotoSaver())
            model.found(FriendLink.invite(code: "ABCD-2345").url)
            return model
        }

        static func notifications() -> NotificationsModel {
            let settings = ReminderSettings(defaults: defaults())
            settings.seasonEnding = true
            return NotificationsModel(
                notifications: FixtureNotifications(), settings: settings, seasons: { seasons }, now: { now },
                timeZone: TimeZone(identifier: "Europe/Minsk") ?? .current)
        }
    }

    // MARK: - Подмены платформы

    /// Медиатека из нарисованных плиток: ограниченный доступ, фото с геотегом и без, несколько видео.
    @MainActor
    final class FixturePhotoLibrary: PhotoLibraryProviding {
        private let colors = PlayerColor.allCases

        func access() -> PhotoAccess { .limited }
        func requestAccess() async -> PhotoAccess { .limited }

        func items(_ filter: GalleryFilter) async -> [GalleryItem] {
            let all = (0..<30).map { index in
                GalleryItem(
                    id: "fixture-\(index)", isVideo: index % 7 == 3, duration: Double(8 + index * 3),
                    createdAt: Date(unix: SheetFixtures.now - Double(index) * 20_000),
                    latitude: index % 3 == 0 ? nil : 52.0976 + Double(index) * 0.001,
                    longitude: index % 3 == 0 ? nil : 23.7341)
            }
            switch filter {
            case .all: return all
            case .geotagged: return all.filter(\.hasLocation)
            case .videos: return all.filter(\.isVideo)
            }
        }

        func thumbnail(for item: GalleryItem, size: CGSize) async -> UIImage? {
            let index = Int(item.id.split(separator: "-").last ?? "0") ?? 0
            let top = colors[index % colors.count].base
            let bottom = colors[(index * 5 + 3) % colors.count].base
            let format = UIGraphicsImageRendererFormat()
            format.scale = 1
            return UIGraphicsImageRenderer(size: size, format: format).image { context in
                let cg = context.cgContext
                let gradient = CGGradient(
                    colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                    colors: [top.cgColor, bottom.cgColor] as CFArray, locations: [0, 1])
                if let gradient {
                    cg.drawLinearGradient(
                        gradient, start: .zero, end: CGPoint(x: size.width, y: size.height), options: [])
                }
                // Петля следа поверх — как фото экрана забега.
                let replay = TrailReplay(
                    coordinates: TrailReplay.sampleLoop(around: ReplayTrack.brest, pointCount: 40 + index),
                    width: size.width, height: size.height, padding: size.width * 0.22, frameCount: 2)
                if let first = replay.path.first {
                    cg.move(to: first.cgPoint)
                    replay.path.dropFirst().forEach { cg.addLine(to: $0.cgPoint) }
                    cg.setStrokeColor(RGBA(0xFFFFFF, alpha: 0.85).cgColor)
                    cg.setLineWidth(size.width * 0.025)
                    cg.setLineJoin(.round)
                    cg.strokePath()
                }
            }
        }

        func startCaching(_ items: [GalleryItem], size: CGSize) {}
        func stopCaching(_ items: [GalleryItem], size: CGSize) {}
        func playerItem(for item: GalleryItem) async -> AVPlayerItemBox? { nil }
        func manageLimitedSelection() async {}
    }

    struct FixturePhotoSaver: PhotoSaving {
        func saveImage(_ data: Data) async -> SaveResult { .saved }
        func saveVideo(_ url: URL) async -> SaveResult { .saved }
    }

    /// Сборка ролика в фикстуре не идёт: экран снят посреди сборки (`phase` задан заранее).
    struct FixtureReplayBuilder: ReplayBuilding {
        func build(
            coordinates: [Coordinate], style: ReplayStyle, to url: URL,
            mode: @escaping @MainActor @Sendable (ReplayBuildMode) -> Void,
            progress: @escaping @Sendable (Double) -> Void
        ) async throws {
            mode(.continuedProcessing)
        }
    }

    struct FixtureCalendar: CalendarWriting {
        func add(_ event: CalendarEventDraft) async -> CalendarWriteResult { .added }
    }

    struct FixtureContacts: ContactsReading {
        func contacts(withIdentifiers identifiers: [String]) async -> [FriendContact] {
            [FriendContact(name: "Аня Ковальчук", phone: "+375 29 123-45-67")]
        }
    }

    /// Камеры нет — экран сканера с рамкой и прочитанным кодом.
    @MainActor
    final class FixtureCamera: CameraControlling {
        var session: AVCaptureSession? { nil }
        func access() -> CameraAccess { .allowed }
        func requestAccess() async -> CameraAccess { .allowed }
        func start(onCode: @escaping @MainActor (String) -> Void) -> Bool { true }
        func stop() {}
        func capturePhoto() async -> Data? { nil }
    }

    @MainActor
    final class FixtureNotifications: LocalNotifying {
        func access() async -> NotificationAccess { .allowed }
        func requestAccess() async -> NotificationAccess { .allowed }
        func schedule(_ reminder: ReminderDraft) async throws {}
        func cancel(_ id: String) {}
    }
#endif
