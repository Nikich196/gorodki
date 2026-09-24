import GameCore
import Networking
import Sync
import Testing

@testable import Gorodki

/// «Старт» в приложении (docs/architecture/sync.md, «Трекер»): флаг занятости живёт в `RunController`, а не в пакете
/// `Sync`, поэтому проверяется здесь — в запущенном приложении на симуляторе. Без флага отказ второго «Старта»
/// останавливал геопозицию и датчики идущего забега (ревью #86).
///
/// На симуляторе CI никто не вошёл и геопозиция не разрешена: «Старт», прошедший флаг, останавливается на проверке
/// разрешения (`StartProblem`) — без системных запросов, без записи забега и без источников.
@MainActor
@Suite("«Старт» в приложении: второй отклоняется, пока первый не закончил", .serialized)
struct RunControllerTests {
    @Test("Второй «Старт», пока первый ещё начинается, отклоняется, а первый доходит до проверки разрешений")
    func secondStartWhileFirstIsStarting() async throws {
        let controller = RunController.shared
        try await waitUntilIdle(controller)

        // Второй встаёт в очередь главного актора и выполнится, когда первый отпустит его на первом `await`.
        let second = Task { try await controller.start(league: .run) }
        var firstError: (any Error)?
        do {
            try await controller.start(league: .run)
        } catch {
            firstError = error
        }
        var secondError: (any Error)?
        do {
            try await second.value
        } catch {
            secondError = error
        }

        // Кто из двух успел первым, решает очередь главного актора; проверяется, что дальше флага прошёл ровно один.
        let errors = [firstError, secondError]
        let rejected = errors.filter { $0 as? TrackerError == .alreadyRunning }
        let checked = errors.filter { $0 is RunController.StartProblem }
        #expect(rejected.count == 1 && checked.count == 1, "итоги двух «Стартов»: \(errors)")
    }

    @Test("«Старт», пока забег продолжается после перезапуска, отклоняется сразу — продолжение закрыло бы и его")
    func startWhileResumingAtLaunch() async throws {
        let controller = RunController.shared
        try await waitUntilIdle(controller)

        // Без `await` между ними: продолжение успевает только встать в очередь, а флаг уже поднят.
        controller.resumeAtLaunch()
        var rejection: (any Error)?
        do {
            try await controller.start(league: .run)
        } catch {
            rejection = error
        }
        #expect(rejection as? TrackerError == .alreadyRunning, "итог «Старта»: \(String(describing: rejection))")

        // Продолжение закончилось — флаг снят, «Старт» снова доходит до проверки разрешений.
        try await waitUntilIdle(controller)
    }

    /// Продолжение после перезапуска держит `RunController` занятым, пока читает базу, а запускает его и сам запуск
    /// приложения, в котором идут тесты. Ждём, пока «Старт» перестанет отвечать «уже идёт».
    private func waitUntilIdle(_ controller: RunController) async throws {
        // С вошедшим игроком и разрешённой геопозицией «Старт» записал бы настоящий забег.
        let signedIn = await AppDependencies.shared.tokens.current()
        try #require(signedIn == nil, "на симуляторе вошёл игрок — тесты «Старта» здесь не запускаются")

        let deadline = ContinuousClock.now + .seconds(10)
        while ContinuousClock.now < deadline {
            do {
                try await controller.start(league: .run)
            } catch TrackerError.alreadyRunning {
                try await Task.sleep(for: .milliseconds(50))
                continue
            } catch {
                return
            }
            Issue.record("«Старт» без входа состоялся: \(controller.state)")
            return
        }
        Issue.record("RunController занят дольше 10 секунд")
    }
}
