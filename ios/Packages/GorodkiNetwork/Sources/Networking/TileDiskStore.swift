import CZlib
import Foundation

/// Кто сейчас вошёл — для кэшей тайлов на диске: файлы у каждого игрока свои (видимые версии и туман — свои).
public enum SessionOwner: Equatable, Sendable {
    case signedIn(playerId: String)
    /// Вход точно не выполнен (хранилище токенов прочитано, входа нет): файлы прежнего игрока стираются.
    case signedOut
    /// Хранилище токенов сейчас недоступно (Keychain до первой разблокировки): это ещё не выход — ничего не стирать.
    case unknown
}

/// Тайлы на диске (PLAN.md §7.2: «кэш тайлов и растры тумана — в Caches»): карта рисуется без сети сразу после
/// запуска, а первый запрос идёт с известными версиями, а не целиком. Раскладка:
/// `Caches/gorodki-tiles/v1/<игрок>/<кэш>/<x>_<y>.tile` (docs/architecture/ios-app.md).
public struct TileCacheLocation: Sendable {
    /// Поколение формата файлов: всё остальное в корне — от прежних форматов и стирается.
    static let generation = "v1"

    public let root: URL

    public init(root: URL) {
        self.root = root
    }

    /// `Library/Caches/gorodki-tiles`: iOS может очистить его сама при нехватке места — это только кэш.
    public static func live() -> TileCacheLocation {
        let caches =
            FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return TileCacheLocation(root: caches.appendingPathComponent("gorodki-tiles", isDirectory: true))
    }

    func store(owner: String, cache: String) -> TileDiskStore {
        TileDiskStore(
            directory: root.appendingPathComponent(Self.generation, isDirectory: true)
                .appendingPathComponent(Self.ownerKey(owner), isDirectory: true)
                .appendingPathComponent(cache, isDirectory: true))
    }

    /// Стереть все тайлы всех игроков: выход и удаление аккаунта (в тайлах тумана — где игрок бывал).
    public func removeAll() {
        try? FileManager.default.removeItem(at: root)
    }

    /// Оставить только тайлы `owner`: папки прежних игроков и прежних форматов стираются.
    func removeOwners(except owner: String) {
        let manager = FileManager.default
        let generationURL = root.appendingPathComponent(Self.generation, isDirectory: true)
        for entry in (try? manager.contentsOfDirectory(atPath: root.path)) ?? [] where entry != Self.generation {
            try? manager.removeItem(at: root.appendingPathComponent(entry))
        }
        let kept = Self.ownerKey(owner)
        for entry in (try? manager.contentsOfDirectory(atPath: generationURL.path)) ?? [] where entry != kept {
            try? manager.removeItem(at: generationURL.appendingPathComponent(entry))
        }
    }

    /// Имя папки игрока: идентификатор (UUID) как есть, иначе — hex его байтов: имя файла не должно зависеть от того,
    /// что прислал сервер.
    static func ownerKey(_ owner: String) -> String {
        let plain =
            (1...64).contains(owner.utf8.count)
            && owner.unicodeScalars.allSatisfy { $0.isASCII && (CharacterSet.alphanumerics.contains($0) || $0 == "-") }
        return plain ? owner : "h-" + owner.utf8.map { String(format: "%02x", $0) }.joined()
    }
}

/// Кэш тайлов на диске для одного `TerritoryCache` или `FogCache`: где лежат файлы и кто сейчас вошёл.
public struct TileCacheDisk: Sendable {
    public var location: TileCacheLocation
    public var owner: @Sendable () async -> SessionOwner

    public init(location: TileCacheLocation, owner: @escaping @Sendable () async -> SessionOwner) {
        self.location = location
        self.owner = owner
    }
}

/// Тайл в файле — конверт формата 1 (JSON). Контрольная сумма покрывает и заголовок: испорченная цифра версии иначе
/// подняла бы версию, телефон спрашивал бы `@7`, отвергал настоящую пятую как «старую» и остался бы с устаревшим тайлом
/// навсегда.
struct TileFile: Codable, Equatable, Sendable {
    static let currentFormat = 1

    var format: Int
    var x: Int
    var y: Int
    var version: Int64
    /// Когда тайл пришёл с сервера целиком, мс (земля: от этого считается угасание); у тумана — время записи.
    var loadedAtMs: Int64
    /// Когда файл записан, мс: при обрезке кэша первыми уходят самые давние.
    var savedAtMs: Int64
    /// Туман — открыто клеток (сверяется после распаковки); у земли `nil`.
    var cellCount: Int?
    /// Земля — JSON участков, туман — биты как пришли (raw DEFLATE); у пустого тумана — пусто.
    var payload: Data
    var crc32: UInt32

    init(x: Int, y: Int, version: Int64, loadedAtMs: Int64, savedAtMs: Int64, cellCount: Int?, payload: Data) {
        self.format = Self.currentFormat
        self.x = x
        self.y = y
        self.version = version
        self.loadedAtMs = loadedAtMs
        self.savedAtMs = savedAtMs
        self.cellCount = cellCount
        self.payload = payload
        self.crc32 = 0
        self.crc32 = checksum()
    }

    func checksum() -> UInt32 {
        let header = "\(format)|\(x)|\(y)|\(version)|\(loadedAtMs)|\(savedAtMs)|\(cellCount ?? -1)|"
        let bytes = Array(header.utf8) + Array(payload)
        let sum = bytes.withUnsafeBufferPointer { buffer in
            CZlib.crc32(0, buffer.baseAddress, uInt(buffer.count))
        }
        return UInt32(truncatingIfNeeded: sum)
    }
}

/// Файлы тайлов одного кэша одного игрока. Операции синхронные: их зовёт актор кэша, и запись не может вклиниться
/// между его шагами (например, после `reset`).
struct TileDiskStore: Sendable {
    enum Read: Equatable {
        case missing
        /// Файл не читается, не того формата, не того тайла или не сошлась контрольная сумма — он уже стёрт.
        case corrupted
        case ok(TileFile)
    }

    let directory: URL

    func url(x: Int, y: Int) -> URL { directory.appendingPathComponent("\(x)_\(y).tile") }

    func read(x: Int, y: Int) -> Read {
        let url = url(x: x, y: y)
        guard let data = try? Data(contentsOf: url) else { return .missing }
        guard let file = try? JSONDecoder().decode(TileFile.self, from: data), file.format == TileFile.currentFormat,
            file.x == x, file.y == y, file.crc32 == file.checksum()
        else {
            try? FileManager.default.removeItem(at: url)
            return .corrupted
        }
        return .ok(file)
    }

    func write(_ file: TileFile) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var options: Data.WritingOptions = [.atomic]
        #if canImport(Darwin)
            // Не `completeFileProtection`: туман и земля пишутся и после «Финиша» на заблокированном телефоне.
            options.insert(.completeFileProtectionUntilFirstUserAuthentication)
        #endif
        try JSONEncoder().encode(file).write(to: url(x: file.x, y: file.y), options: options)
    }

    func delete(x: Int, y: Int) {
        try? FileManager.default.removeItem(at: url(x: x, y: y))
    }

    func removeAll() {
        try? FileManager.default.removeItem(at: directory)
    }

    /// Имена файлов тайлов; всё прочее в папке (временные файлы атомарной записи, прерванной выгрузкой приложения)
    /// стирается: другого писателя в этой папке, пока жив актор кэша, нет.
    func tileNames() -> [String] {
        let manager = FileManager.default
        var names: [String] = []
        for entry in (try? manager.contentsOfDirectory(atPath: directory.path)) ?? [] {
            if entry.hasSuffix(".tile") {
                names.append(entry)
            } else {
                try? manager.removeItem(at: directory.appendingPathComponent(entry))
            }
        }
        return names
    }

    /// Оставить не больше `capacity` файлов: первыми уходят давно записанные (при равном времени — по имени).
    /// Файлы читаются, только если их больше предела.
    /// - Returns: сколько стёрто.
    @discardableResult
    func trim(capacity: Int) -> Int {
        let names = tileNames()
        guard names.count > capacity else { return 0 }
        let manager = FileManager.default
        let dated = names.map { name -> (savedAtMs: Int64, name: String) in
            let url = directory.appendingPathComponent(name)
            let saved = (try? Data(contentsOf: url)).flatMap { try? JSONDecoder().decode(TileFile.self, from: $0) }
            return (saved?.savedAtMs ?? .min, name)
        }
        let oldest = dated.sorted { ($0.savedAtMs, $0.name) < ($1.savedAtMs, $1.name) }.prefix(names.count - capacity)
        for entry in oldest {
            try? manager.removeItem(at: directory.appendingPathComponent(entry.name))
        }
        return oldest.count
    }
}

/// Привязка кэша к вошедшему игроку — общая для земли и тумана.
struct TileDiskBinding: Sendable {
    let disk: TileCacheDisk
    let cache: String
    private(set) var owner: String?
    private(set) var store: TileDiskStore?

    init(disk: TileCacheDisk, cache: String) {
        self.disk = disk
        self.cache = cache
    }

    /// Привязать к тому, кто сейчас вошёл.
    /// - Returns: игрок сменился (или вышел) — память кэша нужно сбросить: версии прежнего новому не подходят.
    mutating func bind(_ state: SessionOwner, capacity: Int) -> Bool {
        switch state {
        case .signedIn(let playerId):
            guard playerId != owner else { return false }
            disk.location.removeOwners(except: playerId)
            let store = disk.location.store(owner: playerId, cache: cache)
            store.trim(capacity: capacity)
            self.owner = playerId
            self.store = store
            return true
        case .signedOut:
            // Не через `store`: вышедший мог так и не привязаться в этом запуске (событие входа теряется, если
            // приложение выгрузили до его обработки), а файлы его тумана — где он бывал — остались бы на диске.
            disk.location.removeAll()
            let changed = owner != nil
            owner = nil
            store = nil
            return changed
        case .unknown:
            return false
        }
    }

    /// Выход или смена аккаунта (`reset` кэша): стереть файлы всех игроков — не только привязанного.
    mutating func forget() {
        disk.location.removeAll()
        owner = nil
        store = nil
    }
}

extension TileFile {
    static func milliseconds(_ seconds: Double) -> Int64 { Int64((seconds * 1_000).rounded()) }
}
