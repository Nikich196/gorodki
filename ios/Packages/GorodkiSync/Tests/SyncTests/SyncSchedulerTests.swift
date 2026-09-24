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

    @Test("Сотни неудач подряд (забег без связи, проход на каждый кусок) — шаг держится на 15 минутах, без падения")
    func endlessFailuresStayAtLongest() {
        var backoff = SyncBackoff()
        var waits: [SyncWake] = []
        for attempt in 0..<300 {
            waits.append(
                backoff.next(
                    after: Self.report(attempt.isMultiple(of: 7) ? .rateLimited : .offline), backlog: .init(),
                    appActive: false))
        }

        #expect(waits.dropFirst(6).allSatisfy { $0 == .after(.seconds(900)) })
        #expect(backoff.failures == 7)  // на потолке счёт не растёт
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

    // MARK: - Фоновые пробуждения

    @Test("Фон: недоставленные забеги — оба пробуждения, только заявки без итога — короткое, пусто — ничего")
    func backgroundRequestsFollowTheBacklog() {
        let quarter = BackgroundSyncPlan.minimumDelay
        #expect(
            BackgroundSyncPlan.requests(after: .after(.seconds(15)), backlog: .init(unfinishedDeliveries: 1))
                == [BackgroundRequest(.upload, after: quarter), BackgroundRequest(.refresh, after: quarter)])
        #expect(
            BackgroundSyncPlan.requests(after: .idle, backlog: .init(unsettledClaims: 2))
                == [BackgroundRequest(.refresh, after: quarter)])
        #expect(BackgroundSyncPlan.requests(after: .idle, backlog: .init()).isEmpty)
    }

    @Test("Фон: не раньше решения расписания и не чаще раза в 15 минут")
    func backgroundRequestsRespectTheWake() {
        let backlog = SyncBacklog(unfinishedDeliveries: 1)
        #expect(
            BackgroundSyncPlan.requests(after: .after(.seconds(3_600)), backlog: backlog)
                == [
                    BackgroundRequest(.upload, after: .seconds(3_600)),
                    BackgroundRequest(.refresh, after: .seconds(3_600)),
                ])
        #expect(
            BackgroundSyncPlan.requests(after: .after(.seconds(900)), backlog: backlog).map(\.earliest)
                == [.seconds(900), .seconds(900)])
        #expect(
            BackgroundSyncPlan.requests(after: .after(.seconds(899)), backlog: backlog).map(\.earliest)
                == [.seconds(900), .seconds(900)])
    }

    @Test(
        "Фон: вход истёк, часы сбиты, аккаунт удаляется — не будить: без игрока проход бесполезен",
        arguments: [SyncWake.needsSignIn, .blocked(.clockInvalid), .blocked(.accountDeleting)])
    func backgroundRequestsWaitForThePlayer(_ wake: SyncWake) {
        #expect(
            BackgroundSyncPlan.requests(after: wake, backlog: .init(unfinishedDeliveries: 3, unsettledClaims: 1))
                .isEmpty)
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

    @Test("Фоновая задача: шаг повторов сначала, но истёкший вход она не обходит")
    func backgroundTaskResetsBackoffButNotSignIn() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store)
        let scheduler = scheduler()

        await server.fail("start", with: .offline)
        #expect(await scheduler.trigger(.appActive) == .after(.seconds(15)))
        await server.fail("start", with: .offline)
        #expect(await scheduler.trigger(.timer) == .after(.seconds(30)))
        await server.fail("start", with: .offline)
        #expect(await scheduler.trigger(.backgroundTask) == .after(.seconds(15)))

        await server.answerStart(of: run.id, status: 401, code: "")
        #expect(await scheduler.trigger(.timer) == .needsSignIn)
        let requests = await server.log.count
        #expect(await scheduler.trigger(.backgroundTask) == .needsSignIn)
        #expect(await server.log.count == requests)
        #expect(await scheduler.backgroundRequests == [])
    }

    @Test("После прохода — какие фоновые пробуждения просить: нет сети — оба, доставлено с заявкой — короткое")
    func backgroundRequestsAfterPasses() async throws {
        try await Fixture.record(Fixture.run(), points: 25, into: store, claims: [Fixture.loop(2, 14)])
        let scheduler = scheduler()
        let quarter = BackgroundSyncPlan.minimumDelay
        #expect(await scheduler.backgroundRequests == nil)  // прохода не было: прежние заявки не трогать

        await server.fail("start", with: .offline)
        await scheduler.trigger(.backgroundTask)
        #expect(
            await scheduler.backgroundRequests
                == [BackgroundRequest(.upload, after: quarter), BackgroundRequest(.refresh, after: quarter)])

        await scheduler.trigger(.backgroundTask)
        #expect(await server.log.contains("finish"))
        #expect(await scheduler.backgroundRequests == [BackgroundRequest(.refresh, after: quarter)])

        await scheduler.stop()
        #expect(await scheduler.backgroundRequests == [])  // вышел — заявки снимаются
    }

    @Test("Фоновая задача во время прохода ждёт его и следующего — пробуждения по свежей очереди, а не «не знаю»")
    func backgroundTaskWaitsForRunningPass() async throws {
        try await Fixture.record(Fixture.run(), points: 25, into: store, claims: [Fixture.loop(2, 14)])
        let scheduler = scheduler()
        let background = Box()
        await server.whileStarting {
            await background.set(
                Task {
                    await scheduler.trigger(.backgroundTask)
                    return await scheduler.backgroundRequests
                })
            try? await Task.sleep(for: .milliseconds(100))  // задача успевает встать в очередь за идущим проходом
        }

        await scheduler.trigger(.appActive)
        let requests = await (try #require(await background.task)).value

        #expect(await server.log.contains("finish"))
        #expect(requests == [BackgroundRequest(.refresh, after: BackgroundSyncPlan.minimumDelay)])
    }

    actor Box {
        private(set) var task: Task<[BackgroundRequest]?, Never>?
        func set(_ task: Task<[BackgroundRequest]?, Never>) { self.task = task }
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
