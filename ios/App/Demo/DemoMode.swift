import Foundation
import GameCore
import Sync

/// «Посмотреть демо» (решение Никиты 07.10): все вкладки на встроенных образцах — земля и туман над Брестом, рейтинги,
/// клан, профиль — без сервера и без входа. Страхует показ: сервер спит, нет сети. Данные те же, что у режима фикстур
/// для снимков (`App/Fixtures`, `contracts/samples`); «Старт» проигрывает короткий забег с захватом петли.
@MainActor
enum DemoMode {
    /// Оболочка демо: вкладка «Карта», игрок из образца `me.json`, земля и туман — `MapFixture`.
    static func shell() -> ShellModel {
        let profile = Fixtures.profile("player-map")
        let map = Fixtures.map(.map, fixture: "player-map", profile: profile)
        let driver = DemoRunDriver()
        let run = RunScreenModel(
            profile: profile, driver: driver, access: DemoStartAccess(),
            makeResult: { id in
                RunResultModel(runId: id, justFinished: true, source: FixtureRunResults(published: true))
            },
            defaults: UserDefaults(suiteName: "demo.run") ?? .standard)
        driver.model = run
        return ShellModel(
            tab: .map, profile: profile, run: run, map: map, home: map.home,
            social: Fixtures.social(.leaderboards, fixture: nil))
    }
}

/// Разрешения в демо: ничего не спрашиваем — забег ненастоящий, GPS не нужен.
@MainActor
final class DemoStartAccess: RunStartAccess {
    func needsPrimer(_ permission: RunStartPermission) -> Bool { false }
    var locationDenied: Bool { false }
    func request(_ permission: RunStartPermission) async {}
}

/// Забег демо: петля вокруг квартала из `RunFixture` — HUD сразу, через несколько секунд петля замыкается (церемония,
/// первая фаза), ещё через пару секунд сервер «подтверждает» захват (вторая фаза). «Финиш» — итог забега.
@MainActor
final class DemoRunDriver: RunDriving {
    /// Экран забега, которому демо отдаёт снимки (как `RunController.onState` — живому).
    weak var model: RunScreenModel?
    private var script: Task<Void, Never>?
    /// Сколько секунд от «Старта» до замыкания петли и до решения сервера.
    static let closesAfter: Duration = .seconds(6)
    static let decidedAfter: Duration = .seconds(3)

    func startRun(league: League) async throws {
        script?.cancel()
        model?.present(RunFixture.runningState(), trail: RunFixture.trail, contours: [])
        script = Task { [weak self] in
            try? await Task.sleep(for: Self.closesAfter)
            guard !Task.isCancelled, let model = self?.model else { return }
            let contour = [LoopContour(claimNo: 0, coordinates: RunFixture.loop)]
            let item = CeremonyItem(runId: RunFixture.runId, loop: RunFixture.claimedLoop)
            model.present(
                RunFixture.runningState(), trail: RunFixture.trail, contours: contour, ceremony: item,
                ring: RunFixture.loop)
            try? await Task.sleep(for: Self.decidedAfter)
            guard !Task.isCancelled, let model = self?.model else { return }
            var decided = item
            decided.decision = CeremonyDecision(
                run: RunFixture.runId, claimNo: 0, applied: true, takenSquareMeters: RunFixture.loopSquareMeters,
                refusalCode: nil)
            model.present(
                RunFixture.runningState(), trail: RunFixture.trail, contours: contour, ceremony: decided,
                ring: RunFixture.loop)
        }
    }

    func finish() async throws {
        script?.cancel()
        var ended = RunFixture.runningState()
        ended.isRunning = false
        model?.present(ended, trail: RunFixture.trail)
    }

    func trail() async -> [[Coordinate]] { RunFixture.trail }

    func ring(claimNo: Int) async -> LoopRing? { nil }

    func requestFullAccuracy() async -> Bool { true }
}
