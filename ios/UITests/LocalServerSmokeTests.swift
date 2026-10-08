import XCTest

/// Связка с настоящим сервером: регистрация по коду, вход тестового игрока и профиль с сервера. Только по флагу — нужен
/// локальный сервер с `Auth:DevSignIn` и инвайтом TEST-2026 (docs/guides/local-server.md); сборка — с
/// `GORODKI_SERVER_URL=http://127.0.0.1:5080`, запуск — `TEST_RUNNER_GORODKI_LOCAL_SMOKE=1 xcodebuild test …`.
final class LocalServerSmokeTests: XCTestCase {
    private static let timeout: TimeInterval = 20

    override func setUp() {
        continueAfterFailure = false
    }

    @MainActor
    func testRegisterSignInAndLoadProfileFromServer() throws {
        guard ProcessInfo.processInfo.environment["GORODKI_LOCAL_SMOKE"] == "1" else {
            throw XCTSkip("Нужен локальный сервер: docs/guides/local-server.md")
        }
        let app = XCUIApplication()
        app.launchArguments += [
            "-AppleLanguages", "(ru)", "-AppleLocale", "ru_RU", "-GorodkiDevSignIn",
            "smoke-\(UUID().uuidString.prefix(8))",
        ]
        app.launch()
        allowSystemAlerts()

        for _ in 0..<2 {
            tap(button(app, "Дальше"), "Нет «Дальше» на интро")
        }
        tap(button(app, "Начать"), "Нет «Начать» на интро")

        let code = app.textFields.firstMatch
        XCTAssertTrue(code.waitForExistence(timeout: Self.timeout), "Нет поля кода приглашения")
        code.tap()
        code.typeText("TEST-2026")
        tap(button(app, "Дальше"), "Код: нет «Дальше»")

        tap(toggle(app, "Мне 16 лет или больше"), "Нет отметки 16+")
        tap(button(app, "Дальше"), "16+: нет «Дальше»")
        tap(toggle(app, "Принимаю пользовательское соглашение"), "Нет отметки соглашения")
        tap(button(app, "Дальше"), "Правила: нет «Дальше»")
        for _ in 0..<4 { app.swipeUp() }  // отметка — под текстом согласия
        tap(toggle(app, "Даю согласие"), "Нет отметки согласия")
        tap(button(app, "Дальше"), "Согласие: нет «Дальше»")
        tap(button(app, "Войти через Google"), "Нет «Войти через Google»")

        let profile = app.tabBars.buttons["Профиль"]
        XCTAssertTrue(profile.waitForExistence(timeout: Self.timeout), "После входа не открылись вкладки")
        profile.tap()
        XCTAssertTrue(element(app, "Открыто тумана").waitForExistence(timeout: Self.timeout), "Профиль не загрузился")
        XCTAssertFalse(element(app, "Нет сети").exists, "Профиль не дошёл до сервера")
        let shot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        shot.name = "smoke-profile"
        shot.lifetime = .keepAlways
        add(shot)
    }

    // MARK: - Помощники

    /// Системные вопросы (геопозиция, «Движение и фитнес», уведомления) — разрешить, чтобы не закрывали экран.
    @MainActor
    private func allowSystemAlerts() {
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        for _ in 0..<3 {
            let allow = springboard.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Разрешить'")).firstMatch
            guard allow.waitForExistence(timeout: 4) else { return }
            allow.tap()
        }
    }

    /// Нажать первый элемент, по которому можно нажать: экраны онбординга лежат стопкой, и кнопка «Дальше» прошлого
    /// шага остаётся в дереве — по ней нажимать нельзя.
    @MainActor
    private func tap(_ query: XCUIElementQuery, _ message: String) {
        let deadline = Date.now.addingTimeInterval(Self.timeout)
        repeat {
            if let target = query.allElementsBoundByIndex.first(where: { $0.exists && $0.isHittable }) {
                target.tap()
                return
            }
            _ = XCTWaiter.wait(for: [XCTestExpectation(description: "экран")], timeout: 0.5)
        } while Date.now < deadline
        let shot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        shot.name = "smoke-failure"
        shot.lifetime = .keepAlways
        add(shot)
        let tree = XCTAttachment(string: XCUIApplication().debugDescription)
        tree.name = "smoke-failure-tree"
        tree.lifetime = .keepAlways
        add(tree)
        XCTFail(message)
    }

    @MainActor
    private func button(_ app: XCUIApplication, _ label: String) -> XCUIElementQuery {
        app.buttons.matching(NSPredicate(format: "label CONTAINS %@ AND enabled == true", label))
    }

    /// Отметка (`CheckmarkToggleStyle`: кнопка с признаком `.isToggle` — для XCUITest это Switch), а не текст документа
    /// с той же фразой.
    @MainActor
    private func toggle(_ app: XCUIApplication, _ label: String) -> XCUIElementQuery {
        app.switches.matching(NSPredicate(format: "label CONTAINS %@", label))
    }

    @MainActor
    private func element(_ app: XCUIApplication, _ fragment: String) -> XCUIElement {
        app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS %@", fragment)).firstMatch
    }
}
