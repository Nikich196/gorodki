import Charts
import DesignSystem
import GameCore
import Networking
import Persistence
import SwiftUI

/// Сколько занимает одна часть данных на телефоне.
struct FileCount: Equatable, Sendable {
    var bytes: Int64 = 0
    var files = 0
}

/// Всё, что показывает «Хранилище», одним снимком.
struct StorageSnapshot: Equatable, Sendable {
    var territory = FileCount()
    var fog = FileCount()
    var replays = FileCount()
    /// База GRDB: очередь синхронизации и история забегов (с журналом WAL).
    var database = FileCount()
    var exports = FileCount()
    /// Забеги, ещё не дошедшие до сервера, и забеги в истории — их очистка кэша не трогает.
    var unsentRuns = 0
    var historyRuns = 0
}

/// Откуда «Хранилище» берёт размеры и как чистит кэш: в приложении — `AppDependencies`, в фикстурах — числа.
struct StorageSource: Sendable {
    var measure: @Sendable () async -> StorageSnapshot
    var clearCache: @Sendable () async -> Void

    static let live = StorageSource(
        measure: {
            let dependencies = AppDependencies.shared
            let tiles = dependencies.tileLocation?.usage() ?? TileCacheUsage()
            let replays = DiskUsage.of(ReplayStore.folder)
            let exports = DiskUsage.of(dependencies.exports.url)
            let database = (try? AppDatabase.liveFile()).map(DiskUsage.database(at:)) ?? 0
            return StorageSnapshot(
                territory: FileCount(bytes: tiles.territory.bytes, files: tiles.territory.files),
                fog: FileCount(bytes: tiles.fog.bytes + tiles.other.bytes, files: tiles.fog.files + tiles.other.files),
                replays: FileCount(bytes: replays.bytes, files: replays.files),
                database: FileCount(bytes: database, files: 1),
                exports: FileCount(bytes: exports.bytes, files: exports.files),
                unsentRuns: await dependencies.unsentRunCount(),
                historyRuns: (try? await dependencies.history?.entries().count) ?? 0)
        },
        clearCache: {
            await AppDependencies.shared.clearCaches()
        })
}

/// «Хранилище» (пункт 5 листика: кэш во внутренней FS): тайлы земли и тумана (`TileDiskStore`), видео-повторы, база
/// GRDB, выгрузки. Очистка стирает только кэш — очередь неотправленных забегов и историю не трогает.
@MainActor
@Observable
final class StorageModel {
    enum Kind: String, CaseIterable, Identifiable {
        case territory, fog, replays, database, exports

        var id: String { rawValue }

        /// Кэш: его можно стереть — земля и туман снова придут с сервера, видео соберётся заново.
        var isCache: Bool { self == .territory || self == .fog || self == .replays }
    }

    var snapshot = StorageSnapshot()
    var loaded = false
    var clearing = false
    var message: String?

    @ObservationIgnored let source: StorageSource

    init(source: StorageSource) {
        self.source = source
    }

    static func live() -> StorageModel { StorageModel(source: .live) }

    func usage(_ kind: Kind) -> FileCount {
        switch kind {
        case .territory: snapshot.territory
        case .fog: snapshot.fog
        case .replays: snapshot.replays
        case .database: snapshot.database
        case .exports: snapshot.exports
        }
    }

    var totalBytes: Int64 { Kind.allCases.reduce(0) { $0 + usage($1).bytes } }
    var cacheBytes: Int64 { Kind.allCases.filter(\.isCache).reduce(0) { $0 + usage($1).bytes } }

    func load() async {
        snapshot = await source.measure()
        loaded = true
    }

    func clearCache() async {
        clearing = true
        let freed = cacheBytes
        await source.clearCache()
        snapshot = await source.measure()
        clearing = false
        message = "Освобождено \(NumberText.bytes(max(freed - cacheBytes, 0))). Неотправленные забеги на месте."
    }
}

struct StorageView: View {
    @State private var model: StorageModel
    @State private var clearAsked = false
    /// Доля круга, которую уже заняла диаграмма: 0 → 1 при появлении.
    @State private var sweep = 0.0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(model: StorageModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        TokenList {
            Section {
                chart
                    .frame(height: 220)
                    .padding(.vertical, 8)
                ForEach(StorageModel.Kind.allCases) { kind in
                    legendRow(kind)
                }
            } footer: {
                Text(
                    "Кэш лежит в Library/Caches: iOS может очистить его сама, когда мало места. База и выгрузки — не кэш."
                )
            }
            Section {
                Button(role: .destructive) {
                    clearAsked = true
                } label: {
                    if model.clearing {
                        ProgressView()
                    } else {
                        Label("Очистить кэш карты и видео", systemImage: "trash")
                    }
                }
                .disabled(model.clearing || model.cacheBytes == 0)
                if let message = model.message {
                    Text(message)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            } footer: {
                Text(queueNote)
            }
        }
        .navigationTitle("Хранилище")
        .confirmationDialog("Очистить кэш?", isPresented: $clearAsked, titleVisibility: .visible) {
            Button("Очистить \(NumberText.bytes(model.cacheBytes))", role: .destructive) {
                Task { await model.clearCache() }
            }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text("Земля и туман загрузятся с сервера заново, видео-повторы сотрутся. \(queueNote)")
        }
        .refreshable { await model.load() }
        .task {
            if !model.loaded { await model.load() }
            withAnimation(Motion.numericAppear.unlessReduceMotion(reduceMotion)) { sweep = 1 }
        }
    }

    private var queueNote: String {
        "Неотправленные забеги (\(model.snapshot.unsentRuns)) и история (\(model.snapshot.historyRuns)) — в базе, "
            + "очистка их не трогает."
    }

    private var chart: some View {
        let total = Double(model.totalBytes)
        return Chart {
            ForEach(StorageModel.Kind.allCases) { kind in
                SectorMark(
                    angle: .value("Байты", Double(model.usage(kind).bytes) * sweep),
                    innerRadius: .ratio(0.64), angularInset: 1.5
                )
                .cornerRadius(4)
                .foregroundStyle(kind.color)
            }
            // Пустая часть круга, пока диаграмма «набегает» при появлении, и весь круг у пустого хранилища.
            SectorMark(
                angle: .value("Байты", total == 0 ? 1 : total * (1 - sweep)), innerRadius: .ratio(0.64),
                angularInset: 1.5
            )
            .foregroundStyle(Palette.uiFill.color)
        }
        .chartLegend(.hidden)
        .chartBackground { _ in
            VStack(spacing: 2) {
                Text(NumberText.bytes(model.totalBytes))
                    .font(.role(.statTile))
                    .foregroundStyle(Palette.uiInk.color)
                    .contentTransition(.numericText(value: total))
                Text("на телефоне")
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Занято \(NumberText.bytes(model.totalBytes))")
        .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.snapshot)
    }

    private func legendRow(_ kind: StorageModel.Kind) -> some View {
        let usage = model.usage(kind)
        return HStack(spacing: 12) {
            Circle()
                .fill(kind.color)
                .frame(width: 12, height: 12)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(kind.title)
                    .foregroundStyle(Palette.uiInk.color)
                Text(kind.detail(files: usage.files))
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            Text(NumberText.bytes(usage.bytes))
                .monospacedDigit()
                .foregroundStyle(Palette.uiInk2.color)
                .contentTransition(.numericText(value: Double(usage.bytes)))
                .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: usage.bytes)
        }
        .accessibilityElement(children: .combine)
    }
}

extension StorageModel.Kind {
    var title: String {
        switch self {
        case .territory: "Земля — тайлы карты"
        case .fog: "Туман «Исследования»"
        case .replays: "Видео-повторы"
        case .database: "База: очередь и история"
        case .exports: "Выгрузки в «Файлах»"
        }
    }

    func detail(files: Int) -> String {
        switch self {
        case .territory, .fog: "Кэш · файлов: \(files)"
        case .replays: "Кэш · роликов: \(files)"
        case .database: "GRDB, SQLite — не кэш"
        case .exports: "Documents/Exports · файлов: \(files)"
        }
    }

    /// Цвета — токены дизайн-системы: туман — своей охрой, остальное — различимыми семантическими цветами.
    var color: Color {
        switch self {
        case .territory: Palette.hotScale[0].color
        case .fog: FogStyle.exploreFill.color
        case .replays: Palette.hot.color
        case .database: Palette.neutral.color
        case .exports: Palette.gold.color
        }
    }
}
