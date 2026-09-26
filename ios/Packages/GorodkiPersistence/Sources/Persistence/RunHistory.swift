import Foundation
import GRDB
import GameCore
import Sync

/// Законченный забег в истории на телефоне — итог без точек.
public struct RunHistoryEntry: Codable, Sendable, Hashable, Identifiable {
    public var id: UUID
    public var league: League
    public var source: LocalRun.Source
    /// Начало и конец по часам телефона, мс.
    public var startedAtMs: Int64
    public var endedAtMs: Int64
    /// Сколько принятых точек сохранено.
    public var pointCount: Int

    public init(
        id: UUID, league: League, source: LocalRun.Source, startedAtMs: Int64, endedAtMs: Int64, pointCount: Int
    ) {
        self.id = id
        self.league = league
        self.source = source
        self.startedAtMs = startedAtMs
        self.endedAtMs = endedAtMs
        self.pointCount = pointCount
    }
}

/// История забегов (GRDB): итог и принятые точки каждого законченного забега. Очередь синхронизации стирает куски, как
/// только сервер подтвердил забег, — выгрузить след (GPX) после этого можно только отсюда. Пишется после «Финиша»
/// (`archiveEnded`), стирается при выходе из аккаунта и его удалении (`removeAll`).
public struct RunHistory: Sendable {
    /// Сколько последних забегов хранить; `nil` — все, пока историю не сотрут (выход, удаление аккаунта). Число
    /// не выбрано — решение Никиты.
    public static let defaultRetention: Int? = nil

    private let writer: any DatabaseWriter
    /// Сколько последних забегов (по началу) хранить; `nil` — без предела.
    public let retention: Int?

    public init(_ database: AppDatabase, retention: Int? = RunHistory.defaultRetention) {
        writer = database.writer
        self.retention = retention
    }

    /// Сохранить законченные забеги очереди, которых ещё нет в истории, — с точками их кусков. Забег, куски которого
    /// очередь уже стёрла (сервер подтвердил его раньше), пропускается: точек уже нет. Пробные забеги «Лаборатории»
    /// (`LocalRun.isLabProbe`) — тоже: история — забеги игрока.
    /// - Returns: сохранённые сейчас забеги.
    @discardableResult
    public func archiveEnded(from queue: any SyncStore) async throws -> [UUID] {
        let known = Set(try await entries().map(\.id))
        var archived: [UUID] = []
        for run in try await queue.runs() where !known.contains(run.id) && !run.confirmedComplete && !run.isLabProbe {
            guard let endedAtMs = run.endedAtMs else { continue }
            var points: [TrackPoint] = []
            for point in try await queue.chunks(of: run.id).flatMap(\.points).sorted(by: { $0.seq < $1.seq })
            where point.seq != points.last?.seq {
                points.append(point)
            }
            let entry = RunHistoryEntry(
                id: run.id, league: run.league, source: run.source, startedAtMs: run.startedAtMs,
                endedAtMs: endedAtMs, pointCount: points.count)
            let row = try HistoryRow(entry, points: points)
            try await writer.write { db in try row.insert(db, onConflict: .ignore) }
            archived.append(run.id)
        }
        try await trim()
        return archived
    }

    /// Забеги истории, новые первыми. Нечитаемая строка пропускается.
    public func entries() async throws -> [RunHistoryEntry] {
        try await writer.read { db in
            try HistoryRow.order(Column("startedAtMs").desc).fetchAll(db).compactMap { try? $0.entry() }
        }
    }

    /// Точки забега по номеру; `nil` — забега в истории нет или строка нечитаема.
    public func points(of id: UUID) async throws -> [TrackPoint]? {
        try await writer.read { db in
            try HistoryRow.fetchOne(db, key: id.uuidString).flatMap { try? $0.decodedPoints() }
        }
    }

    /// Стереть историю — и нечитаемые строки тоже.
    public func removeAll() async throws {
        _ = try await writer.write { db in try HistoryRow.deleteAll(db) }
    }

    private func trim() async throws {
        guard let retention else { return }
        try await writer.write { db in
            try db.execute(
                sql: """
                    DELETE FROM runHistory WHERE id NOT IN
                    (SELECT id FROM runHistory ORDER BY startedAtMs DESC, id LIMIT ?)
                    """,
                arguments: [max(retention, 0)])
        }
    }
}

private struct HistoryRow: Codable, FetchableRecord, PersistableRecord {
    static let databaseTableName = "runHistory"

    var id: String
    var startedAtMs: Int64
    var summary: Data
    var points: Data

    init(_ entry: RunHistoryEntry, points: [TrackPoint]) throws {
        id = entry.id.uuidString
        startedAtMs = entry.startedAtMs
        summary = try JSONEncoder().encode(entry)
        self.points = try JSONEncoder().encode(points)
    }

    func entry() throws -> RunHistoryEntry { try JSONDecoder().decode(RunHistoryEntry.self, from: summary) }

    func decodedPoints() throws -> [TrackPoint] { try JSONDecoder().decode([TrackPoint].self, from: points) }
}
