import Foundation
import Testing

@testable import GorodkiAPI

/// «Пыточный набор» спайка S6: ответы, записанные самим сервером (`contracts/samples`, их держит `ApiSamplesTests`
/// на сервере), разбираются сгенерированными типами без потерь.
@Suite("Клиент API: образцы ответов сервера (contracts/samples)")
struct SampleDecodingTests {
    @Test("Забег: null, диапазоны номеров, перечисления строками")
    func runs() throws {
        let active = try Samples.decode(Components.Schemas.RunResponse.self, "run-active")
        let finished = try Samples.decode(Components.Schemas.RunResponse.self, "run-finished")

        #expect(active.status == .active)
        #expect(active.endedAtMs == nil && active.lastSeq == nil)
        #expect(active.received.first?.lastSeq == 119)
        #expect(active.newcomer)
        #expect(finished.league == .bike && finished.source == .replay)
        #expect(finished.endedAtMs == 1_790_003_600_000)
        #expect(finished.missing.map(\.firstSeq) == [60])
    }

    @Test("Заявки: ожидание, итог со словарём площадей, отказ с кодом")
    func captures() throws {
        let pending = try Samples.decode(Components.Schemas.CaptureResponse.self, "capture-pending")
        let applied = try Samples.decode(Components.Schemas.CaptureResponse.self, "capture-applied")
        let rejected = try Samples.decode(Components.Schemas.CaptureResponse.self, "capture-rejected")

        #expect(pending.status == .pending && pending.waitingFor == "sensors")
        #expect(pending.areaByOutcome == nil && pending.changedTiles == nil)
        #expect(applied.status == .applied)
        #expect(applied.areaByOutcome?.additionalProperties["transferred"] == 5_002.2)
        #expect(applied.changedTiles?.map(\.x) == [684, 685])
        #expect(applied.effectiveAtMs == 1_790_000_612_000)
        #expect(rejected.rejectCode == "segment_broken:vehicle")
    }

    @Test("Карта: вложенные массивы координат, призрак, пустые поля")
    func territory() throws {
        let map = try Samples.decode(Components.Schemas.TerritoryResponse.self, "territory")
        let tile = try #require(map.tiles.first)

        #expect(map.league == .run)
        #expect(tile.version == 3 && tile.parcels.count == 2)
        #expect(tile.parcels[0].exterior.count == 10 && tile.parcels[0].holes.first?.count == 8)
        #expect(tile.parcels[0].siegeUntilMs == nil && tile.parcels[0].shieldUntilMs != nil)
        #expect(tile.contestedZones.first?.untilMs == 1_790_086_800_000 && tile.contestedZones.first?.exterior.count == 10)
        #expect(tile.parcels[1].ghost && tile.parcels[1].level == 0)
        #expect(map.unchanged.map(\.x) == [685])
    }

    @Test("Конфиг целиком: правила лиг, пустой суточный потолок, разрывы по лигам")
    func config() throws {
        let config = try Samples.decode(Components.Schemas.ConfigResponse.self, "config")

        #expect(config.version == 1 && config.activeFromMs == 0)
        #expect(config.rules.leagues.run.maxAccuracyMeters == 25)
        #expect(config.rules.leagues.bike.vehicleShare?.minShare == 0.6)
        #expect(config.rules.capture.maxDailyAreaSquareMeters == nil)
        #expect(config.rules.exploration.maxGapMeters.bike == 200)
    }

    @Test("Туман: сжатые биты тайла в Base64, итог по слоям, туман забега")
    func fog() throws {
        let fog = try Samples.decode(Components.Schemas.FogResponse.self, "fog")
        let summary = try Samples.decode(Components.Schemas.FogSummaryResponse.self, "fog-summary")
        let run = try Samples.decode(Components.Schemas.RunResponse.self, "run-finished")

        #expect(fog.layer == .foot && fog.season == 0 && fog.unchanged.map(\.x) == [9_271])
        let tile = try #require(fog.tiles.first)
        #expect(tile.cellCount == 55 && tile.version == 2)
        #expect(!tile.bits.data.isEmpty)
        #expect(summary.layers.first?.areaSquareMeters == 42_580.5 && summary.layers.first?.season == nil)
        #expect(summary.layers.last?.season == 0)  // сезонный слой; nil — за всё время
        #expect(run.fogNewCells == 1_234 && run.visitedParcels == nil)  // визиты — только когда все точки на месте
    }

    @Test("Ошибки: код для приложения и дополнительные поля (problems, overlaps)")
    func problems() throws {
        let invalid = try Samples.decode(Components.Schemas.ProblemDetails.self, "problem-chunk-invalid")
        let conflict = try Samples.decode(Components.Schemas.ProblemDetails.self, "problem-chunk-conflict")
        let notFound = try Samples.decode(Components.Schemas.ProblemDetails.self, "problem-run-not-found")

        #expect(invalid.code == "chunk_invalid" && invalid.status == 400)
        #expect((invalid.additionalProperties["problems"]?.value as? [Any])?.count == 2)
        #expect(conflict.code == "chunk_conflict")
        #expect((conflict.additionalProperties["overlaps"]?.value as? [Any])?.count == 1)
        #expect(notFound.code == "run_not_found" && notFound.additionalProperties.isEmpty)
    }

    @Test("Вход, профиль, квитанция куска")
    func small() throws {
        let session = try Samples.decode(Components.Schemas.SessionResponse.self, "session")
        let me = try Samples.decode(Components.Schemas.MeResponse.self, "me")
        let receipt = try Samples.decode(Components.Schemas.ChunkReceipt.self, "chunk-receipt")

        #expect(session.expiresIn == 900 && session.isNewUser)
        #expect(me.displayName == "Бегун-1234" && !me.publicProfile)
        #expect(receipt.duplicate && receipt.lastSeq == 119)
    }

    @Test("Своя статистика и карточка игрока: числа, пустые поля, псевдоним")
    func statsAndPlayer() throws {
        let stats = try Samples.decode(Components.Schemas.MyStatsResponse.self, "me-stats")
        let player = try Samples.decode(Components.Schemas.PlayerResponse.self, "player")

        #expect(stats.runs == 12 && stats.distanceMeters == 42_195.5)
        #expect(stats.season == 0 && stats.explorationRank == 7)
        #expect(stats.seasonExploredSquareMeters == 321_000.4)
        #expect(player.name.hasPrefix("Игрок #") && !player.isMe && player.colorIndex == 7)
    }

    @Test("Сезоны: у последнего нет конца, текущий — номер")
    func seasons() throws {
        let seasons = try Samples.decode(Components.Schemas.SeasonsResponse.self, "seasons")

        #expect(seasons.current == 0 && seasons.seasons.count == 2)
        #expect(
            seasons.seasons[0].name == "Сезон 0 (бета)" && seasons.seasons[0].endsAtMs == seasons.seasons[1].startsAtMs)
        #expect(seasons.seasons[1].endsAtMs == nil)
    }
}

/// Образцы из `contracts/samples` (путь — от этого файла вверх до корня репозитория).
enum Samples {
    static func data(_ name: String) throws -> Data {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = directory.appendingPathComponent("contracts/samples/\(name).json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return try Data(contentsOf: candidate)
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: name])
    }

    static func decode<T: Decodable>(_ type: T.Type, _ name: String) throws -> T {
        try JSONDecoder().decode(type, from: data(name))
    }
}
