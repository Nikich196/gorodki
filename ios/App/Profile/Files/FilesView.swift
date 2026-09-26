import DesignSystem
import GameCore
import Persistence
import SwiftUI
import UniformTypeIdentifiers

/// GPX — тип файлов для `fileExporter` и `fileImporter`: у системы своего объявления GPX может не быть, поэтому — по
/// расширению, как XML.
extension UTType {
    static let gpx = UTType(filenameExtension: "gpx", conformingTo: .xml) ?? .xml
}

/// Файл GPX для `fileExporter`: «Сохранить в Файлы» — в любую папку, в том числе iCloud Drive.
struct GPXDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.gpx]

    var data: Data

    init(data: Data) {
        self.data = data
    }

    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

/// След из чужого GPX (`fileImporter`) — из него собирается видео-повтор.
struct ImportedTrack: Equatable, Sendable {
    var name: String
    var coordinates: [Coordinate]

    /// Длина следа, м.
    var meters: Double {
        zip(coordinates, coordinates.dropFirst()).reduce(0) { $0 + Geodesy.distance(from: $1.0, to: $1.1) }
    }
}

/// Откуда «Файлы и экспорт» берут списки: в приложении — `AppDependencies`, в фикстурах — образцы.
struct FilesSource: Sendable {
    var exports: @Sendable () -> [StoredFile]
    var runs: @Sendable () async -> [RunHistoryEntry]
    /// GPX забега в `Exports/`; `nil` — забега в истории нет.
    var exportRun: @Sendable (UUID) async -> URL?

    static let live = FilesSource(
        exports: { AppDependencies.shared.exports.files() },
        runs: { (try? await AppDependencies.shared.history?.entries()) ?? [] },
        exportRun: { try? await AppDependencies.shared.exportRunGPX($0) })
}

/// «Файлы и экспорт» (пункт 6 листика: внешняя FS): папка `Exports/` в «Файлах», GPX забега — `fileExporter`,
/// чужой GPX — `fileImporter` (security-scoped), автоэкспорт каждого забега в выбранную папку, «Мои данные».
@MainActor
@Observable
final class FilesModel {
    var exports: [StoredFile] = []
    var runs: [RunHistoryEntry] = []
    var autoFolderName: String?
    var lastAutoExport: AutoExport.Record?
    var imported: ImportedTrack?
    var message: String?
    var loaded = false
    /// Документ для `fileExporter` и его имя.
    var document: GPXDocument?
    var documentName = "gorodki.gpx"
    var exporterShown = false

    @ObservationIgnored let source: FilesSource
    @ObservationIgnored let autoExport: AutoExport

    init(source: FilesSource, autoExport: AutoExport) {
        self.source = source
        self.autoExport = autoExport
    }

    static func live() -> FilesModel { FilesModel(source: .live, autoExport: .shared) }

    func load() async {
        exports = source.exports()
        runs = await source.runs()
        autoFolderName = autoExport.folderName
        lastAutoExport = autoExport.lastRecord
        loaded = true
    }

    /// «Сохранить в Файлы»: GPX забега — в `Exports/` и в `fileExporter`.
    func prepareExport(of run: RunHistoryEntry) async {
        guard let url = await source.exportRun(run.id), let data = try? Data(contentsOf: url) else {
            message = "Точек этого забега на телефоне уже нет."
            return
        }
        document = GPXDocument(data: data)
        documentName = url.lastPathComponent
        exporterShown = true
        exports = source.exports()
    }

    func exported(_ result: Result<URL, any Error>) {
        switch result {
        case .success(let url): message = "Сохранено: \(url.lastPathComponent)."
        case .failure(let error): message = "Не сохранилось: \(error.localizedDescription)"
        }
    }

    /// Папка из `fileImporter` (`.folder`) — включить автоэкспорт.
    func chooseFolder(_ result: Result<[URL], any Error>) {
        do {
            guard let folder = try result.get().first else { return }
            try autoExport.choose(folder: folder)
            autoFolderName = autoExport.folderName
            message = "Каждый законченный забег появится в папке «\(folder.lastPathComponent)»."
        } catch {
            message = "Папку не удалось выбрать: \(error.localizedDescription)"
        }
    }

    func turnOffAutoExport() {
        autoExport.turnOff()
        autoFolderName = nil
    }

    /// Чужой GPX из `fileImporter`: файл вне песочницы — доступ security-scoped только на время чтения.
    func importGPX(_ result: Result<[URL], any Error>) {
        do {
            guard let url = try result.get().first else { return }
            let accessing = url.startAccessingSecurityScopedResource()
            defer {
                if accessing { url.stopAccessingSecurityScopedResource() }
            }
            let text = String(decoding: try Data(contentsOf: url), as: UTF8.self)
            let coordinates = GPX.trackCoordinates(in: text)
            guard coordinates.count >= 2 else {
                message = "В файле нет трека: нужны точки <trkpt>."
                return
            }
            imported = ImportedTrack(name: url.deletingPathExtension().lastPathComponent, coordinates: coordinates)
            message = nil
        } catch {
            message = "Файл не открылся: \(error.localizedDescription)"
        }
    }
}

struct FilesView: View {
    @State private var model: FilesModel
    /// Что выбирается в «Файлах»: один `fileImporter` на экран — два на одном экране SwiftUI показывает не всегда.
    @State private var importKind = ImportKind.gpx
    @State private var importerShown = false

    private enum ImportKind {
        case folder, gpx
    }
    private let ru = Locale(identifier: "ru_RU")

    init(model: FilesModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        TokenList {
            Section {
                SheetIntro(
                    systemImage: "folder", title: "Файлы — наружу и внутрь",
                    text:
                        "Выгрузки лежат в «Файлах»: «На iPhone» → «Городки» → Exports. Оттуда их можно отправить куда угодно."
                )
            }
            autoExportSection
            runsSection
            importSection
            exportsSection
            MyDataExportSection()
        }
        .tint(Palette.uiInk.color)
        .navigationTitle("Файлы и экспорт")
        .fileExporter(
            isPresented: Bindable(model).exporterShown, document: model.document, contentType: .gpx,
            defaultFilename: model.documentName
        ) { result in
            model.exported(result)
        }
        .fileImporter(
            isPresented: $importerShown, allowedContentTypes: importKind == .folder ? [.folder] : [.gpx, .xml]
        ) { result in
            switch importKind {
            case .folder: model.chooseFolder(result.map { [$0] })
            case .gpx: model.importGPX(result.map { [$0] })
            }
        }
        .refreshable { await model.load() }
        .task {
            if !model.loaded { await model.load() }
        }
    }

    private var autoExportSection: some View {
        Section {
            if let folder = model.autoFolderName {
                ValueRow(title: "Папка", value: folder, systemImage: "folder.badge.gearshape")
                if let last = model.lastAutoExport {
                    ValueRow(
                        title: last.error == nil ? "Последний" : "Не вышло",
                        value: last.error ?? last.fileName, systemImage: last.error == nil ? "checkmark" : "xmark")
                }
                Button("Выключить автоэкспорт", role: .destructive) {
                    model.turnOffAutoExport()
                }
            }
            Button {
                importKind = .folder
                importerShown = true
            } label: {
                Label(model.autoFolderName == nil ? "Выбрать папку" : "Сменить папку", systemImage: "folder.badge.plus")
            }
        } header: {
            Text("Автоэкспорт каждого забега")
        } footer: {
            Text("После «Финиша» GPX забега сам появится в выбранной папке — хоть в iCloud Drive.")
        }
    }

    private var runsSection: some View {
        Section {
            if model.runs.isEmpty {
                Text("Законченных забегов на телефоне пока нет.")
                    .foregroundStyle(Palette.uiInk2.color)
            }
            ForEach(model.runs) { run in
                Button {
                    Task { await model.prepareExport(of: run) }
                } label: {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(
                                Date(unix: Double(run.startedAtMs) / 1_000).formatted(
                                    .dateTime.day().month(.wide).hour().minute().locale(ru))
                            )
                            .foregroundStyle(Palette.uiInk.color)
                            Text(
                                run.league == .run
                                    ? "Бег · точек: \(run.pointCount)" : "Вело · точек: \(run.pointCount)"
                            )
                            .font(.caption)
                            .foregroundStyle(Palette.uiInk2.color)
                        }
                        Spacer()
                        Image(systemName: "square.and.arrow.down")
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
            }
        } header: {
            Text("Забеги — сохранить GPX")
        } footer: {
            if let message = model.message {
                Text(message)
            }
        }
    }

    private var importSection: some View {
        Section {
            Button {
                importKind = .gpx
                importerShown = true
            } label: {
                Label("Открыть GPX из «Файлов»", systemImage: "doc.badge.plus")
            }
            if let track = model.imported {
                NavigationLink {
                    ReplayView(model: .imported(track))
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Видео-повтор: \(track.name)")
                            .foregroundStyle(Palette.uiInk.color)
                        Text(
                            "Точек: \(track.coordinates.count) · "
                                + NumberText.kilometers(fromMeters: track.meters, fractionDigits: 2)
                        )
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk2.color)
                    }
                }
            }
        } header: {
            Text("Импорт")
        } footer: {
            Text("Трек из другого приложения для бега: из него соберётся видео-повтор.")
        }
    }

    private var exportsSection: some View {
        Section {
            if model.exports.isEmpty {
                Text("Папка Exports пока пуста.")
                    .foregroundStyle(Palette.uiInk2.color)
            }
            ForEach(model.exports) { file in
                ShareLink(item: file.url) {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(file.name)
                                .lineLimit(1)
                                .truncationMode(.middle)
                                .foregroundStyle(Palette.uiInk.color)
                            Text(
                                NumberText.bytes(file.bytes) + " · "
                                    + Date(unix: file.modifiedAt).formatted(.relative(presentation: .named).locale(ru))
                            )
                            .font(.caption)
                            .foregroundStyle(Palette.uiInk2.color)
                        }
                        Spacer()
                        Image(systemName: "square.and.arrow.up")
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
            }
        } header: {
            Text("Папка Exports")
        }
    }
}

/// «Мои данные» файлом (`GET /me/export`): всё, что сервер хранит об игроке, — в `Exports/` и «Отправить». В «Файлах
/// и экспорте» для игрока и в «Отладке» — для разбора петель полевого теста (docs/guides/calibration-replay.md).
struct MyDataExportSection: View {
    @State private var file: URL?
    @State private var message: String?
    @State private var working = false

    var body: some View {
        Section {
            Button {
                Task { await export() }
            } label: {
                if working {
                    ProgressView()
                } else {
                    Label("Выгрузить «Мои данные»", systemImage: "square.and.arrow.down")
                }
            }
            .disabled(working)
            if let file {
                ShareLink(item: file) {
                    Label("Отправить файл", systemImage: "square.and.arrow.up")
                }
            }
            if let message {
                Text(message)
                    .foregroundStyle(.secondary)
            }
        } header: {
            Text("Мои данные")
        } footer: {
            Text("JSON со всеми забегами и точками — всё, что сервер хранит об игроке. В нём настоящий маршрут.")
        }
    }

    private func export() async {
        working = true
        message = nil
        defer { working = false }
        do {
            if let url = try await AppDependencies.shared.exportMyData() {
                file = url
            } else {
                message = "В этой сборке не задан адрес сервера."
            }
        } catch {
            message = "Не удалось выгрузить: нужны вход и связь с сервером."
        }
    }
}
