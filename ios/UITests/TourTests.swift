import XCTest

/// Экскурсия по приложению на видимом симуляторе (Никита 15.10: «тестируй у меня на устройстве, сам тыкай, а я слежу»):
/// медленно, с паузами — интро (три карточки), демо целиком, потом живой вход на локальном сервере и экраны, которые
/// ходят на сервер. Снимки — во вложениях, Claude их просматривает. Только по флагу `TEST_RUNNER_GORODKI_TOUR=1` — в CI
/// и в общем прогоне снимков её нет; живой вход — ещё и с локальным сервером (docs/guides/local-server.md).
final class TourTests: XCTestCase {
    private static let timeout: TimeInterval = 20

    override func setUp() {
        continueAfterFailure = true
    }

    @MainActor
    func testIntroAndDemoTour() throws {
        try requireTour()
        let app = launch()

        let next = app.buttons["Дальше"].firstMatch
        XCTAssertTrue(next.waitForExistence(timeout: Self.timeout), "Нет «Дальше» на интро")
        watch(5, "t01-intro-capture")
        next.tap()
        watch(8, "t02-intro-fog")
        next.tap()
        watch(5, "t03-intro-rivals")

        let demo = app.buttons["intro.demo"]
        XCTAssertTrue(demo.waitForExistence(timeout: Self.timeout), "Нет «Посмотреть демо»")
        demo.tap()
        watch(4, "t04-demo-map")

        tap(app.buttons.matching(NSPredicate(format: "label CONTAINS 'Старт'")).firstMatch, "Старт")
        tap(app.buttons.matching(NSPredicate(format: "label CONTAINS 'Начать'")).firstMatch, "Начать")
        watch(3, "t05-demo-hud")
        let confirmed = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label CONTAINS 'подтверждено' OR value CONTAINS 'подтверждено'")).firstMatch
        XCTAssertTrue(confirmed.waitForExistence(timeout: 20), "Церемония не дошла до «подтверждено»")
        watch(2, "t06-demo-ceremony")
        tap(
            app.buttons.matching(NSPredicate(format: "label CONTAINS 'Продолжить забег'")).firstMatch,
            "Продолжить забег")
        tap(app.buttons.matching(NSPredicate(format: "label CONTAINS 'Свернуть'")).firstMatch, "Свернуть")

        tab(app, "Рейтинги")
        watch(2, "t07-demo-leaderboards")
        tap(app.buttons["Исследование"], "Исследование")
        watch(2, "t08-demo-exploration")
        tab(app, "Клан")
        watch(2, "t09-demo-clan")
        tap(app.buttons["Друзья"], "Друзья")
        watch(2, "t10-demo-friends")
        tap(app.buttons["Лента"], "Лента")
        watch(2, "t11-demo-feed")
        tab(app, "Профиль")
        watch(2, "t12-demo-profile")
        app.swipeUp()
        watch(2, "t13-demo-profile-scrolled")
        app.swipeDown()
        tab(app, "Карта")
        tap(app.buttons["demo.exit"], "Выйти из демо")
        XCTAssertTrue(app.buttons["intro.demo"].waitForExistence(timeout: Self.timeout), "Не вернулись на интро")
        watch(2, "t14-back-to-intro")
    }

    @MainActor
    func testLiveSignInTour() throws {
        try requireTour()
        let app = launch(devPlayer: "tour-\(UUID().uuidString.prefix(6))")

        for _ in 0..<2 {
            tap(app.buttons["Дальше"].firstMatch, "Дальше")
            watch(1.5, nil)
        }
        tap(app.buttons["Начать"].firstMatch, "Начать")
        let code = app.textFields.firstMatch
        XCTAssertTrue(code.waitForExistence(timeout: Self.timeout), "Нет поля кода")
        code.tap()
        code.typeText("TEST-2026")
        watch(1.5, "t20-invite")
        tapHittable(
            app.buttons.matching(NSPredicate(format: "label CONTAINS 'Дальше' AND enabled == true")), "Код: Дальше")
        tapHittable(app.switches.matching(NSPredicate(format: "label CONTAINS 'Мне 16 лет'")), "16+")
        watch(1, "t21-age")
        tapHittable(
            app.buttons.matching(NSPredicate(format: "label CONTAINS 'Дальше' AND enabled == true")), "16+: Дальше")
        tapHittable(app.switches.matching(NSPredicate(format: "label CONTAINS 'Принимаю'")), "Соглашение")
        watch(1, "t22-terms")
        tapHittable(
            app.buttons.matching(NSPredicate(format: "label CONTAINS 'Дальше' AND enabled == true")), "Правила: Дальше")
        for _ in 0..<4 { app.swipeUp() }
        tapHittable(app.switches.matching(NSPredicate(format: "label CONTAINS 'Даю согласие'")), "Согласие")
        watch(1, "t23-consent")
        tapHittable(
            app.buttons.matching(NSPredicate(format: "label CONTAINS 'Дальше' AND enabled == true")), "Согласие: Дальше"
        )
        watch(1.5, "t24-sign-in")
        tapHittable(app.buttons.matching(NSPredicate(format: "label CONTAINS 'Войти через Google'")), "Войти")

        XCTAssertTrue(app.tabBars.buttons["Профиль"].waitForExistence(timeout: Self.timeout), "Не открылись вкладки")
        allowSystemAlerts()
        watch(4, "t25-live-map")
        tab(app, "Рейтинги")
        watch(3, "t26-live-leaderboards")
        tab(app, "Клан")
        watch(3, "t27-live-clan")
        tab(app, "Профиль")
        watch(3, "t28-live-profile")

        // Настройки: согласие на показ ника — сервер теперь его сохраняет (#71).
        let settings = app.buttons.matching(NSPredicate(format: "label CONTAINS 'Настройки'")).firstMatch
        if !settings.exists { app.swipeUp() }
        tap(settings, "Настройки")
        watch(2, "t29-live-settings")
        let nick = app.switches.matching(NSPredicate(format: "label CONTAINS 'Показывать мой ник'")).firstMatch
        if nick.waitForExistence(timeout: Self.timeout) {
            nick.tap()
            watch(3, "t30-live-nick-on")
            XCTAssertFalse(
                app.staticTexts.matching(NSPredicate(format: "label CONTAINS 'не работает' OR label CONTAINS 'Ошибка'"))
                    .firstMatch.exists,
                "Согласие на ник не сохранилось")
        } else {
            XCTFail("Нет переключателя «Показывать мой ник»")
        }

        // Выход — следующая экскурсия снова начнётся с интро.
        for _ in 0..<3 { app.swipeUp() }
        tap(app.buttons.matching(NSPredicate(format: "label == 'Выйти'")).firstMatch, "Выйти")
        tap(app.buttons.matching(NSPredicate(format: "label == 'Выйти'")).firstMatch, "Выйти — подтверждение")
        XCTAssertTrue(app.buttons["intro.demo"].waitForExistence(timeout: Self.timeout), "После выхода нет интро")
        watch(2, "t31-signed-out")
    }

    // MARK: - Помощники

    @MainActor
    private func requireTour() throws {
        guard ProcessInfo.processInfo.environment["GORODKI_TOUR"] == "1" else {
            throw XCTSkip("Экскурсия — только по флагу TEST_RUNNER_GORODKI_TOUR=1")
        }
    }

    @MainActor
    private func launch(devPlayer: String? = nil) -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments += ["-AppleLanguages", "(ru)", "-AppleLocale", "ru_RU"]
        if let devPlayer {
            app.launchArguments += ["-GorodkiDevSignIn", devPlayer]
        }
        app.launch()
        return app
    }

    /// Пауза, чтобы было видно, и снимок во вложения.
    @MainActor
    private func watch(_ seconds: TimeInterval, _ name: String?) {
        _ = XCTWaiter.wait(for: [XCTestExpectation(description: "смотрим")], timeout: seconds)
        guard let name else { return }
        let shot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        shot.name = name
        shot.lifetime = .keepAlways
        add(shot)
    }

    @MainActor
    private func tab(_ app: XCUIApplication, _ name: String) {
        tap(app.tabBars.buttons[name], "вкладка «\(name)»")
    }

    @MainActor
    private func tap(_ element: XCUIElement, _ what: String) {
        guard element.waitForExistence(timeout: Self.timeout) else {
            XCTFail("Нет: \(what)")
            return
        }
        element.tap()
        _ = XCTWaiter.wait(for: [XCTestExpectation(description: "анимация")], timeout: 0.8)
    }

    /// Первый элемент, по которому можно нажать (экраны онбординга лежат стопкой).
    @MainActor
    private func tapHittable(_ query: XCUIElementQuery, _ what: String) {
        let deadline = Date.now.addingTimeInterval(Self.timeout)
        repeat {
            if let target = query.allElementsBoundByIndex.first(where: { $0.exists && $0.isHittable }) {
                target.tap()
                _ = XCTWaiter.wait(for: [XCTestExpectation(description: "анимация")], timeout: 0.8)
                return
            }
            _ = XCTWaiter.wait(for: [XCTestExpectation(description: "экран")], timeout: 0.5)
        } while Date.now < deadline
        XCTFail("Нет: \(what)")
    }

    @MainActor
    private func allowSystemAlerts() {
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        for _ in 0..<3 {
            let allow = springboard.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Разрешить'")).firstMatch
            guard allow.waitForExistence(timeout: 3) else { return }
            allow.tap()
        }
    }
}
