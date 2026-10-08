import XCTest

/// «Посмотреть демо» глазами игрока (решение Никиты 07.10): с интро — во вкладки на образцах, забег с захватом петли,
/// остальные вкладки, выход обратно. Без аргументов режима фикстур — тот же путь, что в IPA. Снимки — 95–99.
final class DemoFlowTests: XCTestCase {
    private static let launchTimeout: TimeInterval = 30
    private static let screenTimeout: TimeInterval = 10

    override func setUp() {
        continueAfterFailure = false
    }

    @MainActor
    func test95DemoFromIntroToCaptureAndBack() {
        let app = XCUIApplication()
        app.launchArguments += ["-AppleLanguages", "(ru)", "-AppleLocale", "ru_RU", "-GorodkiTheme", "day"]
        app.launch()

        let demo = app.buttons["intro.demo"]
        XCTAssertTrue(demo.waitForExistence(timeout: Self.launchTimeout), "На интро нет «Посмотреть демо»")
        demo.tap()

        XCTAssertTrue(
            element(in: app, containing: "Демо: данные-примеры").waitForExistence(timeout: Self.screenTimeout),
            "Нет плашки демо")
        pause(4)  // карта и земля дорисовываются
        snapshot("95-demo-map-day")

        let start = button(in: app, containing: "Старт")
        XCTAssertTrue(start.waitForExistence(timeout: Self.screenTimeout), "На карте нет «Старт»")
        start.tap()
        let begin = button(in: app, containing: "Начать")
        XCTAssertTrue(begin.waitForExistence(timeout: Self.screenTimeout), "Нет листа «Новый забег»")
        begin.tap()

        XCTAssertTrue(
            element(in: app, containing: "Финиш").waitForExistence(timeout: Self.screenTimeout), "HUD не открылся")
        pause(2)
        snapshot("96-demo-hud-day")

        // Петля замыкается через 6 с, сервер «подтверждает» ещё через 3 с (DemoRunDriver).
        XCTAssertTrue(
            element(in: app, containing: "подтверждено").waitForExistence(timeout: 20),
            "Церемония не дошла до «подтверждено»")
        snapshot("97-demo-ceremony-day")

        // Как игрок: карточку церемонии закрывает «Продолжить забег», потом HUD сворачивается в плашку над вкладками.
        let resume = button(in: app, containing: "Продолжить забег")
        XCTAssertTrue(resume.waitForExistence(timeout: Self.screenTimeout), "На карточке нет «Продолжить забег»")
        resume.tap()
        let collapse = button(in: app, containing: "Свернуть")
        XCTAssertTrue(collapse.waitForExistence(timeout: Self.screenTimeout), "В HUD нет «Свернуть»")
        pause(1)
        collapse.tap()
        for (tab, name) in [("Рейтинги", "98-demo-leaderboards-day"), ("Профиль", "99-demo-profile-day")] {
            let item = app.tabBars.buttons[tab]
            XCTAssertTrue(item.waitForExistence(timeout: Self.screenTimeout), "Нет вкладки «\(tab)»")
            item.tap()
            pause(2)
            snapshot(name)
        }

        let exit = app.buttons["demo.exit"]
        XCTAssertTrue(exit.waitForExistence(timeout: Self.screenTimeout), "Нет «Выйти» из демо")
        exit.tap()
        let back = app.buttons["intro.demo"].waitForExistence(timeout: Self.screenTimeout)
        snapshot("100-demo-exit-day")
        XCTAssertTrue(back, "Не вернулись на интро")
    }

    // MARK: - Помощники

    @MainActor
    private func button(in app: XCUIApplication, containing label: String) -> XCUIElement {
        app.buttons.matching(NSPredicate(format: "label CONTAINS %@", label)).firstMatch
    }

    @MainActor
    private func element(in app: XCUIApplication, containing fragment: String) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "label CONTAINS %@ OR value CONTAINS %@", fragment, fragment))
            .firstMatch
    }

    @MainActor
    private func snapshot(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    @MainActor
    private func pause(_ seconds: TimeInterval) {
        _ = XCTWaiter.wait(for: [XCTestExpectation(description: "экран дорисовывается")], timeout: seconds)
    }
}
