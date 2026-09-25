import Foundation
import GameCore
import GorodkiAPI
import Networking
import OpenAPIRuntime
import Sync
import Testing

/// Сквозной прогон: телефон (RunTracker → RunSession → очередь → SyncEngine) против настоящего сервера и базы.
/// Что проверяется и как читать красный — docs/guides/e2e.md. Прогон рассчитан на новую базу: повтор на той же базе
/// с тем же игроком упрётся в проверку «карта чистая».
@Suite(
    "Сквозной прогон против настоящего сервера",
    .serialized,
    .enabled(if: E2EEnvironment.isRequested, "нужен сервер: GORODKI_E2E_SERVER, см. docs/guides/e2e.md"),
    .timeLimit(.minutes(10))
)
struct RunToServerTests {
    @Test("Сервер отвечает, правила v1 у сервера и телефона совпадают")
    func serverAndRules() async throws {
        let env = try E2EEnvironment.load()
        let a = try await Player(env.server, token: env.token)
        let b = try await Player(env.server, token: env.otherToken)

        let check = await ServerCheck.run(a.client)
        guard case .online = check else {
            Issue.record("сервер не отвечает: \(check)")
            return
        }
        for player in [a, b] {
            guard case .ok = try await player.client.getMe() else {
                Issue.record("сервер не принял токен игрока \(player.id) — ключ подписи или claims (sign-jwt.py)")
                return
            }
        }
        // Контракт конфига против живого сервера, а не только против contracts/game-config.v1.json.
        let rules = try await RulesStore(api: a.client, storage: InMemoryRulesStorage()).refresh()
        #expect(rules.version == 1)
        #expect(rules.rules == PhoneRules.version1)
    }

    @Test("Сквозной забег: квадрат 100 × 100 м → захват, земля, туман, без дублей, чужой не видит")
    func squareRun() async throws {
        let env = try E2EEnvironment.load()
        let lossy = LossyTransport(base: ClientFactory.urlSessionTransport())
        let a = try await Player(env.server, token: env.token, transport: lossy)
        let b = try await Player(env.server, token: env.otherToken)
        let tileQuery = "\(SyntheticRun.territoryTile.x):\(SyntheticRun.territoryTile.y)"

        // P0. Карта чистая: прогон рассчитан на новую базу.
        let before = try await territory(a.client, tiles: tileQuery)
        try #require(
            before.flatMap(\.parcels).isEmpty,
            "база не чистая — прогон рассчитан на новую базу (docs/guides/e2e.md)")

        // P1. Правила и забег.
        let rules = try await RulesStore(api: a.client, storage: InMemoryRulesStorage()).refresh()
        let synthetic = SyntheticRun()
        let run = LocalRun(
            id: UUID(), ownerId: a.id, league: .run, source: .live, configVersion: rules.version,
            startedAtMs: synthetic.startedAtMs, deviceId: UUID(), appVersion: "e2e-ci", motionAuthorized: true)
        let store = InMemorySyncStore()

        // P2. Запись до петли.
        let newcomer = try await RunSession.isNewcomer(ownerId: a.id, store: store)
        #expect(newcomer)
        let session = try await RunSession.start(run, store: store, rules: rules.rules, newcomer: newcomer)
        let tracker = RunTracker()
        try await tracker.start { session }
        for input in synthetic.inputs(SyntheticRun.walk, offset: 0, speed: 1.4, stepsPerFiveSeconds: 10) {
            tracker.send(input)
        }
        await tracker.flush()
        let walked = await tracker.state
        try #require(walked.stats.loops == 1, "петля не заявлена: \(walked)")
        let queued = await store.claims(of: run.id)
        let claim = try #require(queued.first)
        #expect(queued.count == 1 && claim.claimNo == 0)
        #expect((9_000...11_000).contains(claim.loop.estimatedArea), "\(claim.loop)")

        // P3. Синхронизация посреди забега (так делает onQueued на петле), ответы теряются, как при обрыве.
        let engine = SyncEngine(
            store: store, api: a.client, ownerId: a.id, now: { Date().timeIntervalSince1970 },
            signedInPlayer: { [tokens = a.tokens] in await tokens.current()?.playerId })
        lossy.arm(["startRun": 1, "uploadChunk": 1, "claimLoop": 1])
        var totals = Totals()
        for _ in 0..<8 {
            totals.add(await engine.syncOnce())
            if await store.claims(of: run.id).first?.sent == true { break }
        }
        #expect(totals.offline == 3 && lossy.dropped.count == 3, "обрывы: \(lossy.dropped), \(totals)")
        #expect(totals.otherStops.isEmpty, "\(totals.otherStops)")
        // Кусок запечатан на петле с отметкой датчиков «последний тик − 10 с» — раньше конца петли, забег не завершён:
        // заявка ждёт датчиков, а не решается по тайм-ауту (ClaimReadiness).
        let midRun = try await captures(a.client, run: run.id)
        #expect(midRun.count == 1)
        #expect(midRun.first?.status == .pending && midRun.first?.waitingFor == "sensors", "\(midRun)")

        // P4. Остаток забега: «машина» рвёт след.
        for input in synthetic.inputs(
            SyntheticRun.drive, offset: SyntheticRun.walk.count, speed: 15, stepsPerFiveSeconds: 0)
        {
            tracker.send(input)
        }
        await tracker.flush()
        let drove = await tracker.state
        switch drove.lastEvent {
        case .broken(.noSteps), .broken(.tooFast), .broken(.strideOutOfRange): break
        default: Issue.record("след не порван «машиной»: \(String(describing: drove.lastEvent))")
        }
        try await tracker.finish(at: synthetic.finishAt)
        #expect(await tracker.state.isRunning == false)
        let fog = await session.fog
        let phoneFogCells = await tracker.state.stats.fogCells
        // Снимок очереди «будто ничего не отправлял» — для повтора в P9.
        let replayStore = try await Self.unsentCopy(of: store)
        // После подтверждения очередь куски удаляет — считаем по снимку.
        let chunkCount = await replayStore.chunks(of: run.id).count

        // P5. Доставка с потерянным «Финишем».
        lossy.arm(["finishRun": 1])
        let applied: PendingClaim = try await eventually("заявка решена и забег подтверждён") {
            totals.add(await engine.syncOnce())
            let local = await store.runs().first { $0.id == run.id }
            let pending = await store.claims(of: run.id).first
            let done = local?.confirmedComplete == true && pending?.isSettled == true
            return (done ? pending : nil, "забег \(String(describing: local?.serverState)), заявка \(String(describing: pending?.outcome))")
        }
        #expect(totals.report.startedRuns == 1 && totals.report.sentClaims == 1 && totals.report.finishedRuns == 1, "\(totals)")
        #expect(totals.report.duplicateChunks == 1, "\(totals)")
        #expect(totals.report.uploadedChunks + totals.report.duplicateChunks == chunkCount, "кусков \(chunkCount), \(totals)")
        #expect(totals.anomalies == 0, "\(totals)")
        #expect(totals.offline == 4 && lossy.dropped.count == 4, "обрывы: \(lossy.dropped), \(totals)")
        let outcome = try #require(applied.outcome)
        #expect(outcome.status == "applied" && outcome.rejectCode == nil, "\(outcome)")
        #expect((9_500...10_500).contains(outcome.areaSquareMeters), "\(outcome)")
        #expect(abs(outcome.areaSquareMeters - claim.loop.estimatedArea) <= 0.05 * outcome.areaSquareMeters)

        // P6. Сервер: забег и заявка.
        let local = try #require(await store.runs().first { $0.id == run.id })
        let server = try await getRun(a.client, run.id)
        let lastSeq = try #require(local.lastSeq)
        #expect(server.status == .finished && server.lastSeq.map(Int.init) == lastSeq)
        #expect(server.received.map { [$0.firstSeq, $0.lastSeq] } == [[0, Int32(lastSeq)]], "\(server.received)")
        #expect(server.missing.isEmpty)
        #expect(server.newcomer == true && local.judgedAsNewcomer == true)
        let finalCaptures = try await captures(a.client, run: run.id)
        let capture = try #require(finalCaptures.first)
        #expect(finalCaptures.count == 1 && capture.status == .applied)
        #expect(Int(capture.startSeq) == claim.loop.startSeq && Int(capture.endSeq) == claim.loop.endSeq)
        let changed = Set((capture.changedTiles ?? []).map { TileKey(x: Int($0.x), y: Int($0.y)) })
        #expect(changed == [SyntheticRun.territoryTile], "\(changed)")

        // P7. Земля: автор видит своё сразу (TerritoryReader скрывает свежие захваты только от других).
        let territoryCache = TerritoryCache(api: a.client, league: .run)
        try await territoryCache.refresh(visible: changed)
        let tile = try #require(await territoryCache.tile(SyntheticRun.territoryTile), "тайла земли нет")
        let own = tile.parcels.filter { "\($0.ownerId)".lowercased() == a.id }
        #expect(!own.isEmpty && tile.version >= 1, "\(tile.parcels.count) кусков, версия \(tile.version)")
        #expect(own.allSatisfy { $0.level == 1 && !$0.ghost && $0.colorIndex == 3 })
        let shapes = own.map { ParcelShape(exterior: ParcelShape.ring($0.exterior), holes: $0.holes.map(ParcelShape.ring)) }
        let ownArea = shapes.reduce(0.0) { sum, shape in
            sum + Self.area(shape.exterior) - shape.holes.reduce(0.0) { $0 + Self.area($1) }
        }
        #expect(abs(ownArea - outcome.areaSquareMeters) <= 0.01 * outcome.areaSquareMeters, "земля \(ownArea) м²")
        let center = SyntheticRun.plane.unproject(PlanarPoint(east: 50, north: 50))
        let outside = SyntheticRun.plane.unproject(PlanarPoint(east: 50, north: 200))
        #expect(ParcelHitTest.parcel(at: center, tolerance: 0, among: shapes) != nil)
        #expect(ParcelHitTest.parcel(at: outside, tolerance: 0, among: shapes) == nil)

        // P8. Туман бит в бит: что открыл телефон, то и сервер.
        let fogCache = FogCache(api: a.client, layer: .foot)
        let phoneTiles = Set(fog.tiles.keys.map { FogTileRef(x: $0.x, y: $0.y) })
        try await eventually("туман открыт на сервере") {
            await fogCache.invalidate()
            try await fogCache.refresh(visible: phoneTiles)
            var missing: [FogTileRef] = []
            for key in phoneTiles where (await fogCache.tile(key)?.cellCount ?? 0) == 0 { missing.append(key) }
            return (missing.isEmpty ? () : nil, "без тумана: \(missing)")
        }
        let serverFog = try await fogTiles(a.client)
        #expect(Set(serverFog.map { FogTileRef(x: Int($0.x), y: Int($0.y)) }) == phoneTiles)
        for (key, bits) in fog.tiles {
            let serverWords = await fogCache.tile(FogTileRef(x: key.x, y: key.y))?.words ?? []
            let differing = zip(serverWords, bits.words).reduce(0) { $0 + ($1.0 ^ $1.1).nonzeroBitCount }
            #expect(serverWords == bits.words, "тайл \(key.x):\(key.y): расходится \(differing) клеток")
        }
        let serverCells = serverFog.reduce(0) { $0 + Int($1.cellCount) }
        let runAfterFog = try await getRun(a.client, run.id)
        #expect(serverCells == phoneFogCells, "сервер \(serverCells), телефон \(phoneFogCells)")
        #expect(runAfterFog.fogNewCells.map(Int.init) == phoneFogCells)

        // P9. Повтор без дублей. (a) Та же очередь — ни одного запроса.
        let requestsBefore = lossy.requests.count
        #expect(await engine.syncOnce() == SyncReport())
        #expect(lossy.requests.count == requestsBefore, "\(lossy.requests.dropFirst(requestsBefore))")
        // (b) Тот же телефон, будто ничего не отправлял: сервер всё узнаёт и ничего не задваивает.
        let counting = LossyTransport(base: ClientFactory.urlSessionTransport())
        let replayer = try await Player(env.server, token: env.token, transport: counting)
        let replay = await SyncEngine(
            store: replayStore, api: replayer.client, ownerId: a.id, now: { Date().timeIntervalSince1970 }
        ).syncOnce()
        let replayChunks = chunkCount
        #expect(replay.stop == nil && replay.startedRuns == 1 && replay.uploadedChunks == 0, "\(replay)")
        #expect(replay.duplicateChunks == replayChunks && replay.sentClaims == 1 && replay.finishedRuns == 1, "\(replay)")
        #expect(await replayStore.runs().first?.confirmedComplete == true)
        #expect(counting.count("startRun") == 1 && counting.count("claimLoop") == 1 && counting.count("finishRun") == 1)
        #expect(counting.count("uploadChunk") == replayChunks, "\(counting.requests)")
        let afterReplay = try await captures(a.client, run: run.id)
        #expect(afterReplay.map(\.id) == [capture.id], "заявки после повтора: \(afterReplay.map(\.id))")
        let runAfterReplay = try await getRun(a.client, run.id)
        #expect(runAfterReplay.received == server.received && runAfterReplay.fogNewCells == runAfterFog.fogNewCells)
        await territoryCache.markChanged(changed)
        let territoryUpdated = try await territoryCache.refresh(visible: changed)
        let versionAfterReplay = await territoryCache.tile(SyntheticRun.territoryTile)?.version
        #expect(
            territoryUpdated.isEmpty,
            "версия тайла \(tile.version) → \(String(describing: versionAfterReplay)) после повтора: дубль или визит, см. журнал сервера"
        )
        await fogCache.invalidate()
        #expect(try await fogCache.refresh(visible: phoneTiles).isEmpty)

        // P10. Чужой игрок B: свежий захват скрыт (задержка 20 мин, «скрытое не выдаёт себя»), чужой забег — 404.
        let seenByB = try await territory(b.client, tiles: tileQuery)
        #expect(!seenByB.flatMap(\.parcels).contains { "\($0.ownerId)".lowercased() == a.id })
        if let bTile = seenByB.first {
            #expect(bTile.version < tile.version, "B видит версию \(bTile.version), A — \(tile.version)")
        }
        guard case .notFound = try await b.client.getRun(path: .init(runId: a.runPath(run.id))) else {
            Issue.record("B получил чужой забег")
            return
        }
        #expect(try await fogTiles(b.client).isEmpty)
    }

    // MARK: - Помощники

    /// Площадь кольца на плоскости вокруг начала забега, м².
    private static func area(_ ring: [Coordinate]) -> Double {
        PlanarRing(ring.map { SyntheticRun.plane.project($0) }).area
    }

    /// Копия очереди с поля синхронизации сброшенными: «тот же телефон, будто ничего не отправлял».
    private static func unsentCopy(of store: InMemorySyncStore) async throws -> InMemorySyncStore {
        let copy = InMemorySyncStore()
        for var run in await store.runs() {
            run.serverState = .unknown
            run.finishSent = false
            run.finishRejectCode = nil
            run.confirmedComplete = false
            run.resendRounds = 0
            await copy.insert(run)
            for var chunk in await store.chunks(of: run.id) {
                chunk.sent = false
                await copy.save(chunk)
            }
            for var claim in await store.claims(of: run.id) {
                claim.sent = false
                claim.refusedCode = nil
                claim.outcome = nil
                await copy.save(claim)
            }
        }
        return copy
    }

    private func territory(_ client: Client, tiles: String) async throws -> [Components.Schemas.TileTerritory] {
        guard case .ok(let ok) = try await client.getTerritory(query: .init(league: "run", tiles: tiles)) else {
            throw E2EError.unexpected("GET /territory не 200")
        }
        return try ok.body.json.tiles
    }

    private func captures(_ client: Client, run: UUID) async throws -> [Components.Schemas.CaptureResponse] {
        guard case .ok(let ok) = try await client.listCaptures(path: .init(runId: run.uuidString.lowercased())) else {
            throw E2EError.unexpected("GET /runs/\(run)/captures не 200")
        }
        return try ok.body.json
    }

    private func getRun(_ client: Client, _ run: UUID) async throws -> Components.Schemas.RunResponse {
        guard case .ok(let ok) = try await client.getRun(path: .init(runId: run.uuidString.lowercased())) else {
            throw E2EError.unexpected("GET /runs/\(run) не 200")
        }
        return try ok.body.json
    }

    private func fogTiles(_ client: Client) async throws -> [Components.Schemas.FogTileView] {
        guard case .ok(let ok) = try await client.getFog(query: .init(layer: "foot")) else {
            throw E2EError.unexpected("GET /fog не 200")
        }
        return try ok.body.json.tiles
    }
}

/// Игрок: вход по токену из окружения и клиент тем же путём, что в приложении (AuthMiddleware).
struct Player {
    let id: String
    let tokens: TokenStore
    let client: Client

    init(_ server: URL, token: String, transport: (any ClientTransport)? = nil) async throws {
        let tokens = TokenStore(storage: InMemoryTokenStorage())
        let signed = AuthTokens(accessToken: token, refreshToken: "e2e-unused")
        guard let id = signed.playerId else { throw E2EError.unexpected("в токене нет sub") }
        await tokens.signIn(signed)
        self.id = id.lowercased()
        self.tokens = tokens
        client = ClientFactory.make(
            serverURL: server, tokens: tokens, transport: transport ?? ClientFactory.urlSessionTransport())
    }

    func runPath(_ id: UUID) -> String { id.uuidString.lowercased() }
}

/// Сумма отчётов всех проходов синхронизации.
struct Totals: CustomStringConvertible {
    var report = SyncReport()
    var offline = 0
    var otherStops: [SyncStop] = []

    /// Чего в чистом прогоне быть не должно: куски выброшены, перерезаны, без датчиков, отложены, забыты, дозапрошены.
    var anomalies: Int {
        report.droppedChunks + report.splitChunks + report.strippedChunks + report.deferredRuns + report.forgottenRuns
            + report.requeuedChunks
    }

    mutating func add(_ pass: SyncReport) {
        report.startedRuns += pass.startedRuns
        report.deferredRuns += pass.deferredRuns
        report.uploadedChunks += pass.uploadedChunks
        report.duplicateChunks += pass.duplicateChunks
        report.splitChunks += pass.splitChunks
        report.strippedChunks += pass.strippedChunks
        report.droppedChunks += pass.droppedChunks
        report.sentClaims += pass.sentClaims
        report.finishedRuns += pass.finishedRuns
        report.requeuedChunks += pass.requeuedChunks
        report.forgottenRuns += pass.forgottenRuns
        report.unconfirmedFinishes += pass.unconfirmedFinishes
        switch pass.stop {
        case .offline: offline += 1
        case .some(let stop): otherStops.append(stop)
        case nil: break
        }
    }

    var description: String { "\(report), обрывов \(offline)" }
}
