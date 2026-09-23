import Foundation
import GameCore
import Testing

@testable import Sync

@Suite("Когда синхронизировать: правило повторов и расписание")
struct SyncSchedulerTests {
    private static func report(
        _ stop: SyncStop? = nil, deferred: Int = 0, requeued: Int = 0, storageLimit: Bool = false
    )
        -> SyncReport
    {
        var report = SyncReport()
        report.stop = stop
        report.deferredRuns = deferred
        report.requeuedChunks = requeued
        report.storageLimitReached = storageLimit
        return report
    }

    // MARK: - Правило

    @Test("Нет сети — 15 с, 30 с, 1 мин… до 15 минут; удачный проход — шаг сначала")
    func offlineBacksOffExponentially() {
        var backoff = SyncBackoff()
        let waits = (0..<9).map { _ in backoff.next(after: Self.report(.offline), backlog: .init(), appActive: true) }

        #expect(
            waits == [15, 30, 60, 120, 240, 480, 900, 900, 900].map { SyncWake.after(.seconds($0)) })
        #expect(backoff.next(after: Self.report(), backlog: .init(), appActive: true) == .idle)
        #expect(backoff.next(after: Self.report(.offline), backlog: .init(), appActive: true) == .after(.seconds(15)))
    }

    @Test("Слишком частые запросы — не раньше чем через минуту")
    func rateLimitedWaitsAtLeastAMinute() {
        var backoff = SyncBackoff()
        #expect(
            backoff.next(after: Self.report(.rateLimited), backlog: .init(), appActive: true) == .after(.seconds(60)))
    }

    @Test("Вход истёк — ждать входа; часы и удаление аккаунта — действия игрока")
    func stopsThatNeedThePlayer() {
        var backoff = SyncBackoff()
        #expect(backoff.next(after: Self.report(.unauthorized), backlog: .init(), appActive: true) == .needsSignIn)
        #expect(
            backoff.next(after: Self.report(.clockInvalid), backlog: .init(), appActive: true)
                == .blocked(.clockInvalid))
        #expect(
            backoff.next(after: Self.report(.accountDeleting), backlog: .init(), appActive: true)
                == .blocked(.accountDeleting))
    }

    @Test("Чистый проход: заявки ждут итога — опрос раз в 30 с, пока приложение открыто; иначе ждать нечего")
    func cleanPassPollsOnlyForUnsettledClaims() {
        var backoff = SyncBackoff()
        let waiting = SyncBacklog(unsettledClaims: 1)

        #expect(backoff.next(after: Self.report(), backlog: waiting, appActive: true) == .after(.seconds(30)))
        #expect(backoff.next(after: Self.report(), backlog: waiting, appActive: false) == .idle)
        #expect(backoff.next(after: Self.report(), backlog: .init(), appActive: true) == .idle)
    }

    @Test("Отложенный забег или суточный объём — через час; досылка — почти сразу; из нескольких — ближайшее")
    func cleanPassWaitsForTheNearestReason() {
        var backoff = SyncBackoff()

        #expect(
            backoff.next(after: Self.report(deferred: 1), backlog: .init(), appActive: true) == .after(.seconds(3_600)))
        #expect(
            backoff.next(after: Self.report(storageLimit: true), backlog: .init(), appActive: false)
                == .after(.seconds(3_600)))
        #expect(
            backoff.next(after: Self.report(requeued: 2), backlog: .init(), appActive: true) == .after(.seconds(15)))
        #expect(
            backoff.next(after: Self.report(deferred: 1), backlog: .init(unsettledClaims: 1), appActive: true)
                == .after(.seconds(30)))
    }

    // MARK: - Расписание

    /// Сон таймера: запоминает, на сколько просили уснуть, и не спит (дальше таймер не идёт).
    actor Sleeps {
        private(set) var durations: [Duration] = []
        private var waiters: [(Int, CheckedContinuation<Void, Never>)] = []

        func record(_ duration: Duration) {
            durations.append(duration)
            let ready = waiters.filter { $0.0 <= durations.count }
            waiters.removeAll { $0.0 <= durations.count }
            ready.forEach { $0.1.resume() }
        }

        func waitFor(_ count: Int) async {
            if durations.count >= count { return }
            await withCheckedContinuation { waiters.append((count, $0)) }
        }
    }

    private let store = InMemorySyncStore()
    private let server = FakeServer()
    private let sleeps = Sleeps()

    private func scheduler(appActive: Bool = true) -> SyncScheduler {
        let store = self.store
        let sleeps = self.sleeps
        return SyncScheduler(
            engine: SyncEngine(store: store, api: server, ownerId: Fixture.owner, now: { Fixture.start + 7_200 }),
            backlog: { (try? await SyncBacklog.of(store, ownerId: Fixture.owner)) ?? SyncBacklog() },
            appActive: { appActive },
            sleep: { duration in
                await sleeps.record(duration)
                throw CancellationError()
            })
    }

    @Test("Забег доставлен, заявка ждёт итога — таймер на 30 с")
    func deliveredRunWithPendingClaimPolls() async throws {
        try await Fixture.record(Fixture.run(), points: 25, into: store, claims: [Fixture.loop(2, 14)])
        let scheduler = scheduler()

        let wake = await scheduler.trigger(.recorded)
        await sleeps.waitFor(1)

        #expect(wake == .after(.seconds(30)))
        #expect(await sleeps.durations == [.seconds(30)])
        #expect(await server.log.contains("claim 0"))
    }

    @Test("Нет сети — повтор со всё большим шагом; сеть вернулась — шаг сначала")
    func offlineRetriesGrowAndNetworkResets() async throws {
        try await Fixture.record(Fixture.run(), points: 5, into: store)
        let scheduler = scheduler()

        await server.fail("start", with: .offline)
        #expect(await scheduler.trigger(.appActive) == .after(.seconds(15)))
        await server.fail("start", with: .offline)
        #expect(await scheduler.trigger(.timer) == .after(.seconds(30)))
        await server.fail("start", with: .offline)
        #expect(await scheduler.trigger(.networkRestored) == .after(.seconds(15)))
    }

    @Test("Вход истёк — события без входа ничего не отправляют; вход — снова синхронизация")
    func expiredSignInWaitsForSignIn() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store)
        await server.answerStart(of: run.id, status: 401, code: "")
        let scheduler = scheduler()

        #expect(await scheduler.trigger(.appActive) == .needsSignIn)
        let requests = await server.log.count
        #expect(await scheduler.trigger(.recorded) == .needsSignIn)
        #expect(await scheduler.trigger(.timer) == .needsSignIn)
        #expect(await server.log.count == requests)  // ни одного запроса без входа

        await server.clearStartAnswer(of: run.id)
        #expect(await scheduler.trigger(.signedIn) == .idle)
        #expect(await server.log.contains("finish"))
    }

    @Test("Очередь игрока: недоставленные забеги и заявки без итога")
    func backlogCountsOnlyTheOwnersWork() async throws {
        try await Fixture.record(Fixture.run(), points: 25, into: store, claims: [Fixture.loop(2, 14)])
        try await Fixture.record(Fixture.run(owner: "someone-else"), points: 5, into: store)
        _ = await SyncEngine(store: store, api: server, ownerId: Fixture.owner, now: { Fixture.start + 7_200 })
            .syncOnce()

        let backlog = try await SyncBacklog.of(store, ownerId: Fixture.owner)

        #expect(backlog == SyncBacklog(unfinishedDeliveries: 0, unsettledClaims: 1))
    }
}
