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
        #expect(
            tile.contestedZones.first?.untilMs == 1_790_086_800_000 && tile.contestedZones.first?.exterior.count == 10)
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
        #expect(summary.osmSetVersion == 1 && summary.layers.first?.brestPercent == 1.37)  // «% Бреста» (E9)
        #expect(summary.layers.first?.districts?.first?.kind == .district)
        #expect(summary.layers.first?.districts?.last?.proposal == true)
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

    @Test("Кланы (E5): участники с ролями, код только лидеру, не в клане — когда можно вступить")
    func clans() throws {
        let clan = try Samples.decode(Components.Schemas.ClanResponse.self, "clan")
        let mine = try Samples.decode(Components.Schemas.MyClanResponse.self, "clan-mine")
        let hues = try Samples.decode(Components.Schemas.ClanHuesResponse.self, "clan-hues")

        #expect(clan.hue == 4 && clan.full && clan.myRole == .leader && clan.inviteCode == "K7M2-9QXA")
        #expect(clan.members.map(\.role) == [.leader, .officer, .member])
        #expect(clan.members[1].name.hasPrefix("Игрок #") && clan.members[0].me)
        #expect(mine.clan == nil && mine.canJoinAtMs == 1_790_259_200_000)
        #expect(hues.free.count == 8 && hues.free.first == 0)
    }

    @Test("Рейтинг территории (E7) и Зал славы (E8): очки, предварительный срез, обезличенная строка")
    func territoryBoardAndHallOfFame() throws {
        let board = try Samples.decode(Components.Schemas.TerritoryLeaderboardResponse.self, "leaderboard-territory")
        let hall = try Samples.decode(Components.Schemas.HallOfFameResponse.self, "hall-of-fame")

        #expect(board.league == .run && board.season == 0 && !board.final && board.day == "2026-11-20")
        #expect(board.entries.first?.points == 1_240 && board.mine?.rank == 17 && board.mine?.me == true)
        let season = try #require(hall.seasons.first)
        #expect(season.season == 0 && season.entries.count == 4)
        #expect(season.entries[2].playerId == nil && season.entries[2].name == nil)  // удалил аккаунт
        #expect(season.entries[3].kind == .clan && season.entries[3].clanId != nil)
    }

    @Test("Входящие (E10), Рюкзак (E11), серия (E22), карточка недели (E12)")
    func inboxBackpackStreakWeekly() throws {
        let inbox = try Samples.decode(Components.Schemas.InboxResponse.self, "inbox")
        let inventory = try Samples.decode(Components.Schemas.InventoryResponse.self, "inventory")
        let streak = try Samples.decode(Components.Schemas.StreakResponse.self, "streak")
        let weekly = try Samples.decode(Components.Schemas.WeeklyCardResponse.self, "weekly")

        #expect(inbox.unread == 1 && inbox.nextCursor != nil && inbox.items.first?.kind == .attack)
        #expect(inbox.items.first?.read == false && inbox.items.last?.kind == .streak)
        #expect(inventory.slots == 12 && inventory.items.first?.kind == .radar)
        #expect(inventory.items.first?.remainingMeters == 1_850.5 && inventory.items.last?.remainingMeters == nil)
        #expect(inventory.items.last?.kind == .streakFreeze && inventory.items.last?.active == false)
        #expect(streak.days == 5 && !streak.todayCounted && streak.freezeActive)
        #expect(weekly.weekStart == "2026-11-16" && weekly.distanceMeters == 12_400.3)
        #expect(weekly.brestPercentGained == 1.3 && weekly.capturedSquareMeters == 8_450.2)
    }

    @Test("Друзья (E14a) и лента (E14b): статусы, посты только числами и датой без времени")
    func friendsAndFeed() throws {
        let friends = try Samples.decode(Components.Schemas.FriendsResponse.self, "friends")
        let feed = try Samples.decode(Components.Schemas.FeedResponse.self, "feed")

        #expect(friends.myCode == "R4T8-KD2M" && friends.friends.map(\.status) == [.incoming, .friend])
        #expect(feed.nextCursor == nil && feed.posts.count == 2)
        let capture = feed.posts[0]
        #expect(capture.kind == .capture && capture.capturedSquareMeters == 4_120 && capture.distanceMeters == nil)
        #expect(capture.day == "2026-11-20" && capture.respects == 3 && capture.respectedByMe && !capture.mine)
        #expect(feed.posts[1].kind == .run && feed.posts[1].distanceMeters == 5_230 && feed.posts[1].mine)
    }

    @Test("Коллекция (E15): найденный значок и силуэт с подсказкой — без координат")
    func collection() throws {
        let collection = try Samples.decode(Components.Schemas.CollectionResponse.self, "collection")
        let found = try #require(collection.caches.first)
        let hidden = try #require(collection.caches.last)

        #expect(found.rarity == .epic && found.name != nil && found.finderNumber == 1 && found.goldFrame)
        #expect(hidden.rarity == .legendary && hidden.name == nil && hidden.foundAtMs == nil && !hidden.goldFrame)
        let text = try String(decoding: Samples.data("collection"), as: UTF8.self)
        #expect(!text.contains("\"lat") && !text.contains("\"lon"))  // координат тайников на телефоне нет
    }

    @Test("Короли участков (E16): список с короной, отрезок с линией, таблица")
    func segments() throws {
        let list = try Samples.decode(Components.Schemas.SegmentListResponse.self, "segments")
        let segment = try Samples.decode(Components.Schemas.SegmentResponse.self, "segment")
        let board = try Samples.decode(Components.Schemas.SegmentLeaderboardResponse.self, "segment-leaderboard")

        #expect(list.league == .run && list.segments.count == 2 && list.segments[1].seasonCrown == nil)
        #expect(list.segments[0].seasonCrown?.timeMs == 71_400)
        #expect(segment.line.count == 6 && segment.legend?.efforts == 14 && segment.myBestTimeMs == 80_120)
        #expect(board.season == 0 && board.entries.count == 2 && board.mine?.rank == 9)
    }

    @Test("Дуэли (E17): идущая со счётом, вызов без счёта")
    func duels() throws {
        let duels = try Samples.decode(Components.Schemas.DuelsResponse.self, "duels")
        let active = try #require(duels.duels.first)
        let pending = try #require(duels.duels.last)

        #expect(active.status == .active && active.metric == .distance && active.days == 3)
        #expect(active.me.score == 8_420.5 && active.opponent.score == 9_010 && active.me.staked)
        #expect(active.winnerId == nil && active.endsAtMs == 1_790_259_200_000)
        #expect(pending.status == .pending && pending.metric == .segmentTime && pending.segmentId != nil)
        #expect(pending.me.score == nil && pending.startsAtMs == nil)
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
