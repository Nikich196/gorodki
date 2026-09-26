import AVFoundation
import Foundation
import GameCore
import Networking
import Persistence
import Sync
import Testing
import UIKit

@testable import Gorodki

/// Модели экранов пунктов листика (docs/architecture/ios-app.md, «Пункты листика»): каждая — с подменами платформы,
/// без системных запросов. Логика без iOS — в пакетах (AssignmentSheetLogicTests, TileCacheUsageTests,
/// StorageFilesTests, SyncLogTests), здесь — склейка с экранами и то, что проверяется только на iOS (запись MP4).
@Suite("Пункты листика: модели экранов")
@MainActor
struct AssignmentSheetTests {
    private static func defaults() -> UserDefaults {
        let name = "gorodki.tests.\(UUID().uuidString)"
        return UserDefaults(suiteName: name) ?? .standard
    }

    private static let season0 = SeasonInfo(
        number: 0, name: "Сезон 0 (бета)", startsAtMs: 1_794_776_400_000, endsAtMs: 1_795_986_000_000)
    private static let minsk = TimeZone(identifier: "Europe/Minsk") ?? .current

    // MARK: - «Пункты задания»

    @Test("14 пунктов по таблице §4: частично — 4 и 13, iOS-аналог — 8, остальные сделаны")
    func assignmentItemsFollowThePlan() {
        let items = AssignmentItem.all
        #expect(items.map(\.number) == Array(1...14))
        #expect(items.filter { $0.status == .partial }.map(\.number) == [4, 13])
        #expect(items.filter { $0.status == .analog }.map(\.number) == [8])
        #expect(items.filter { $0.status == .done }.count == 11)
        // У каждого пункта, кроме входа (ждёт Client ID), — экран, куда перейти.
        #expect(items.filter(\.screens.isEmpty).map(\.number) == [1])
    }

    // MARK: - «Хранилище»

    @Test("Хранилище: сумма, кэш отдельно; очистка стирает только кэш и говорит, сколько освободила")
    func storage() async {
        let cleared = Counter()
        let snapshots = SnapshotBox(
            StorageSnapshot(
                territory: FileCount(bytes: 3_000_000, files: 10), fog: FileCount(bytes: 1_000_000, files: 4),
                replays: FileCount(bytes: 2_000_000, files: 1), database: FileCount(bytes: 500_000, files: 1),
                exports: FileCount(bytes: 100_000, files: 2), unsentRuns: 2, historyRuns: 5))
        let model = StorageModel(
            source: StorageSource(
                measure: { await snapshots.value },
                clearCache: {
                    await cleared.add()
                    await snapshots.set(
                        StorageSnapshot(
                            database: FileCount(bytes: 500_000, files: 1), exports: FileCount(bytes: 100_000, files: 2),
                            unsentRuns: 2, historyRuns: 5))
                }))
        await model.load()
        #expect(model.totalBytes == 6_600_000)
        #expect(model.cacheBytes == 6_000_000)
        await model.clearCache()
        #expect(await cleared.value == 1)
        #expect(model.cacheBytes == 0)
        #expect(model.snapshot.unsentRuns == 2, "очередь не тронута")
        #expect(model.message?.contains("6,0\u{00A0}МБ") == true, "\(model.message ?? "")")
    }

    // MARK: - «Резервная копия»

    @Test("Резервная копия: сервер, очередь и журнал; «Синхронизировать сейчас» без входа — объяснение")
    func backup() async {
        let log = SyncLog.inMemory()
        var report = SyncReport()
        report.uploadedChunks = 3
        log.record(report, atMs: 1_000)
        let model = BackupModel(
            source: BackupSource(
                summary: { nil }, queue: { BackupQueue(unsentRuns: 1, unsettledClaims: 0) }, log: { log.current },
                syncNow: { false }))
        await model.load()
        #expect(model.summary == nil)
        #expect(model.queue.unsentRuns == 1)
        #expect(model.log.lastSuccessAtMs == 1_000)
        await model.syncNow()
        #expect(model.message?.contains("после входа") == true)

        let synced = BackupModel(
            source: BackupSource(summary: { nil }, queue: { BackupQueue() }, log: { log.current }, syncNow: { true }))
        await synced.syncNow()
        #expect(synced.message == "Синхронизировано")
    }

    @Test("Выход стирает журнал синхронизации: время чужой синхронизации новому игроку не нужно")
    func wipeClearsSyncLog() async throws {
        let log = SyncLog.inMemory()
        log.record(SyncReport(), atMs: 5_000)
        let dependencies = AppDependencies(serverURL: nil, tokenStorage: InMemoryTokenStorage(), syncLog: log)
        try await dependencies.wipeLocalData()
        #expect(log.current == SyncLog.State())
    }

    // MARK: - «Календарь»

    @Test("Календарь: фаза сезона; добавленное запоминается; отказ — к Настройкам")
    func calendar() async {
        let writer = FakeCalendar(result: .added)
        let defaults = Self.defaults()
        let model = CalendarModel(
            calendar: writer, seasons: { [Self.season0] }, defaults: defaults, now: { 1_795_600_000 })
        await model.load()
        #expect(model.phase(of: Self.season0) == .going)
        await model.add(Self.season0)
        #expect(model.added == [0])
        #expect(writer.events.first?.title == "Городки: конец сезона «Сезон 0 (бета)»")
        let reopened = CalendarModel(calendar: writer, seasons: { [] }, defaults: defaults)
        #expect(reopened.added == [0], "без чтения Календаря игра помнит, что уже добавила")

        let denied = CalendarModel(
            calendar: FakeCalendar(result: .denied), seasons: { [] }, defaults: Self.defaults())
        await denied.add(Self.season0)
        #expect(denied.added.isEmpty)
        #expect(denied.message?.contains("Настройках") == true)
    }

    // MARK: - «Уведомления»

    @Test("Уведомления: разрешение по кнопке ставит напоминание о сезоне; выключили — снято; пробное — через 5 с")
    func notifications() async {
        let center = FakeNotifications(access: .notDetermined)
        let settings = ReminderSettings(defaults: Self.defaults())
        settings.seasonEnding = true
        let model = NotificationsModel(
            notifications: center, settings: settings, seasons: { [Self.season0] }, now: { 1_795_600_000 },
            timeZone: Self.minsk)
        await model.load()
        #expect(model.seasonReminder?.fireAt == 1_795_881_600)
        #expect(center.scheduled.isEmpty, "без разрешения ничего не ставится")
        await model.requestAccess()
        #expect(model.access == .allowed)
        #expect(center.scheduled.map(\.id) == ["season.0.ending"])
        await model.setSeasonEnding(false)
        #expect(center.cancelled == ["season.0.ending"])
        #expect(!settings.seasonEnding)
        await model.sendTest()
        #expect(center.scheduled.last?.fireAt == 1_795_600_005)
    }

    @Test("«Забег всё ещё идёт»: только с идущим забегом и включённым напоминанием")
    func runReminder() {
        let center = FakeNotifications(access: .allowed)
        let previous = RunReminder.notifications
        RunReminder.notifications = center
        defer { RunReminder.notifications = previous }
        let settings = ReminderSettings(defaults: Self.defaults())
        RunReminder.appWentToBackground(runIsGoing: true, settings: settings)
        settings.runStillGoing = true
        RunReminder.appWentToBackground(runIsGoing: false, settings: settings)
        #expect(center.scheduled.isEmpty)
        RunReminder.cancel()
        #expect(center.cancelled == [Reminders.runStillGoingID])
    }

    // MARK: - «Пригласить друга», QR и сканер

    @Test("Приглашение: код запоминается, SMS другу с готовым текстом, выбранный контакт — первый отмеченный")
    func invite() async throws {
        let defaults = Self.defaults()
        let model = InviteModel(contacts: FakeContacts(), defaults: defaults, senderName: "Бегун-1234")
        #expect(model.smsURL == nil, "друга ещё нет")
        model.inviteCode = "abcd-2345"
        #expect(model.codeIsValid)
        #expect(defaults.string(forKey: InviteModel.codeKey) == "abcd-2345")
        await model.picked(["a", "b"])
        #expect(model.friend?.name == "Аня Ковальчук")
        #expect(model.message.hasPrefix("Привет, Аня!"))
        #expect(model.message.contains("ABCD-2345"))
        let sms = try #require(model.smsURL?.absoluteString)
        #expect(sms.hasPrefix("sms:+375291234567&body="))
        #expect(!sms.contains(" "), "текст закодирован")
    }

    @Test("Сканер: нет доступа — Настройки; нет камеры — объяснение; код игры — разобран, повтор не считается")
    func scanner() async {
        let denied = ScannerModel(camera: FakeCamera(access: .denied, hasCamera: true), saver: FakeSaver())
        await denied.start()
        #expect(denied.state == .denied)
        let noCamera = ScannerModel(camera: FakeCamera(access: .notDetermined, hasCamera: false), saver: FakeSaver())
        await noCamera.start()
        #expect(noCamera.state == .unavailable)

        let camera = FakeCamera(access: .allowed, hasCamera: true)
        let model = ScannerModel(camera: camera, saver: FakeSaver())
        await model.start()
        #expect(model.state == .running)
        camera.onCode?("gorodki://invite?code=abcd-2345")
        #expect(model.result == .game(.invite(code: "ABCD-2345")))
        camera.onCode?("gorodki://invite?code=ABCD-2345")
        #expect(model.scans == 1, "тот же код — без второй хаптики")
        camera.onCode?("https://example.com")
        #expect(model.result == .text("https://example.com"))
        #expect(model.scans == 2)
    }

    @Test("QR-код игры рисуется CoreImage")
    func qrImage() throws {
        let image = try #require(QRCodeImage.make(FriendLink.invite(code: "ABCD-2345").url))
        #expect(image.size.width >= 200)
    }

    // MARK: - «Галерея»

    @Test("Галерея: ограниченный доступ, фильтры; кэш миниатюр — видимые и запас, ушедшие отпускаются")
    func gallery() async {
        let library = RecordingLibrary()
        let model = GalleryModel(library: library)
        #expect(model.access == .limited)
        await model.load()
        #expect(model.items.count == 30)
        model.appeared(0)
        #expect(library.started.count == min(30, GalleryModel.preheatMargin + 1))
        await model.select(.videos)
        #expect(model.items.allSatisfy(\.isVideo))
        #expect(!model.items.isEmpty)
        await model.select(.geotagged)
        #expect(model.items.allSatisfy(\.hasLocation))
    }

    // MARK: - Видео-повтор

    @Test("Видео-повтор: обложка — весь след; сборка не вышла — понятная ошибка, способ сборки показан")
    func replayModel() async {
        let builder = FailingBuilder()
        let model = ReplayModel(
            player: .sky, builder: builder, saver: FakeSaver(), track: { .sample })
        await model.load()
        #expect(model.track?.isSample == true)
        #expect(model.poster?.width == ReplayVideoWriter.side)
        await model.build()
        guard case .failed(let text) = model.phase else {
            Issue.record("ожидалась ошибка, а не \(model.phase)")
            return
        }
        #expect(text.hasPrefix("Видео не собралось"))
        await model.savePoster()
        #expect(model.message == "Кадр итога — в «Фото».")
    }

    @Test("MP4 из кадров следа: файл H.264 720×720, 7 секунд, прогресс доходит до конца")
    func replayVideoIsWritten() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("replay-test-\(UUID()).mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        let last = Counter()
        try await ReplayVideoWriter.write(
            coordinates: TrailReplay.sampleLoop(around: ReplayTrack.brest),
            style: ReplayStyle(player: .orange, title: "проверка"), to: url
        ) { fraction in
            if fraction >= 1 { Task { await last.add() } }
        }
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration).seconds
        #expect(abs(duration - 7) < 0.1, "длительность \(duration)")
        let track = try #require(try await asset.loadTracks(withMediaType: .video).first)
        let size = try await track.load(.naturalSize)
        #expect(size == CGSize(width: 720, height: 720))
    }

    // MARK: - «Файлы и экспорт»

    @Test("Автоэкспорт: папка по закладке, GPX копируется туда; выключили — папки нет")
    func autoExport() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("autoexport-\(UUID())")
        let folder = root.appendingPathComponent("Выбранная")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("gorodki-run.gpx")
        try Data("<gpx/>".utf8).write(to: source)

        let export = AutoExport(defaults: Self.defaults())
        #expect(export.folderName == nil)
        try export.choose(folder: folder)
        #expect(export.folderName == "Выбранная")
        let record = export.copy(source)
        #expect(record.error == nil, "\(record.error ?? "")")
        #expect(FileManager.default.fileExists(atPath: folder.appendingPathComponent("gorodki-run.gpx").path))
        export.turnOff()
        #expect(export.folderName == nil)
        #expect(export.copy(source).error != nil)
    }

    @Test("Импорт GPX: трек из файла — в видео-повтор; файл без трека — объяснение")
    func importGPX() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("import-\(UUID())")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let good = root.appendingPathComponent("Утро.gpx")
        try Data(#"<gpx><trkpt lat="52.1" lon="23.7"/><trkpt lat="52.11" lon="23.71"/></gpx>"#.utf8).write(to: good)
        let empty = root.appendingPathComponent("пусто.gpx")
        try Data("<gpx/>".utf8).write(to: empty)

        let model = FilesModel(
            source: FilesSource(exports: { [] }, runs: { [] }, exportRun: { _ in nil }),
            autoExport: AutoExport(defaults: Self.defaults()))
        model.importGPX(.success([empty]))
        #expect(model.imported == nil)
        #expect(model.message?.contains("нет трека") == true)
        model.importGPX(.success([good]))
        #expect(model.imported?.name == "Утро")
        #expect(model.imported?.coordinates.count == 2)
        #expect((1_000...1_500).contains(model.imported?.meters ?? 0))
    }
}

// MARK: - Подмены

private actor Counter {
    private(set) var value = 0
    func add() { value += 1 }
}

private actor SnapshotBox {
    private(set) var value: StorageSnapshot
    init(_ value: StorageSnapshot) { self.value = value }
    func set(_ value: StorageSnapshot) { self.value = value }
}

@MainActor
private final class FakeCalendar: CalendarWriting {
    let result: CalendarWriteResult
    private(set) var events: [CalendarEventDraft] = []

    init(result: CalendarWriteResult) {
        self.result = result
    }

    func add(_ event: CalendarEventDraft) async -> CalendarWriteResult {
        events.append(event)
        return result
    }
}

@MainActor
private final class FakeNotifications: LocalNotifying {
    private var current: NotificationAccess
    private(set) var scheduled: [ReminderDraft] = []
    private(set) var cancelled: [String] = []

    init(access: NotificationAccess) {
        current = access
    }

    func access() async -> NotificationAccess { current }

    func requestAccess() async -> NotificationAccess {
        current = .allowed
        return current
    }

    func schedule(_ reminder: ReminderDraft) async throws { scheduled.append(reminder) }
    func cancel(_ id: String) { cancelled.append(id) }
}

private struct FakeContacts: ContactsReading {
    func contacts(withIdentifiers identifiers: [String]) async -> [FriendContact] {
        identifiers.isEmpty ? [] : [FriendContact(name: "Аня Ковальчук", phone: "+375 (29) 123-45-67")]
    }
}

@MainActor
private final class FakeCamera: CameraControlling {
    let current: CameraAccess
    let hasCamera: Bool
    private(set) var onCode: (@MainActor (String) -> Void)?

    init(access: CameraAccess, hasCamera: Bool) {
        current = access
        self.hasCamera = hasCamera
    }

    var session: AVCaptureSession? { nil }
    func access() -> CameraAccess { current }
    func requestAccess() async -> CameraAccess { current == .notDetermined ? .allowed : current }

    func start(onCode: @escaping @MainActor (String) -> Void) -> Bool {
        self.onCode = onCode
        return hasCamera
    }

    func stop() {}
    func capturePhoto() async -> Data? { nil }
}

private struct FakeSaver: PhotoSaving {
    func saveImage(_ data: Data) async -> SaveResult { .saved }
    func saveVideo(_ url: URL) async -> SaveResult { .saved }
}

private struct FailingBuilder: ReplayBuilding {
    struct Broken: LocalizedError {
        var errorDescription: String? { "нет места" }
    }

    func build(
        coordinates: [Coordinate], style: ReplayStyle, to url: URL,
        mode: @escaping @MainActor @Sendable (ReplayBuildMode) -> Void, progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        mode(.foreground(reason: "проверка"))
        progress(0.5)
        throw Broken()
    }
}

/// Медиатека фикстур, которая запоминает, что просили готовить заранее.
@MainActor
private final class RecordingLibrary: PhotoLibraryProviding {
    private let base = FixturePhotoLibrary()
    private(set) var started: [GalleryItem] = []

    func access() -> PhotoAccess { base.access() }
    func requestAccess() async -> PhotoAccess { await base.requestAccess() }
    func items(_ filter: GalleryFilter) async -> [GalleryItem] { await base.items(filter) }
    func thumbnail(for item: GalleryItem, size: CGSize) async -> UIImage? { nil }
    func startCaching(_ items: [GalleryItem], size: CGSize) { started += items }
    func stopCaching(_ items: [GalleryItem], size: CGSize) {}
    func playerItem(for item: GalleryItem) async -> AVPlayerItemBox? { nil }
    func manageLimitedSelection() async {}
}
