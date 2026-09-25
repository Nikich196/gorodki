import XCTest

/// Снимки экранов, которые открываются без сервера (PLAN.md, §8 и §13: «снимки и UI-тесты — информационно»):
/// стартовый экран с «Проверкой установки» и «Лаборатория» — прогулка (S1), стенд карты (S4), сервер (S7), пробный
/// забег. Идут в ios-snapshots.yml на симуляторе iPhone; снимки — вложения XCTest, workflow выгружает их из пакета
/// результатов в PNG (артефакт запуска): так приложение можно посмотреть без Mac.
///
/// Каждый тест запускает приложение заново — сорвавшийся переход не утянет за собой остальные снимки. Снимок делается
/// до проверки: и неудачный переход виден на картинке. Кнопки ищутся по части надписи («Карта:», «Сервер:»): хвост
/// надписи меняется вместе со спайками, а идентификаторы доступности в экранах ради одних снимков заводить незачем.
/// Приложение на Mac ещё не запускали под XCUITest, поэтому поиск нарочно терпим к устройству дерева доступности:
/// строка списка — кнопка или ячейка, заголовок — имя панели навигации или текст на ней.
///
/// Демо-повтор забега («UI-тесты повтора», PLAN.md, §8) здесь не снимается: ему нужны роль `demo` и сервер — позже.
final class ScreenSnapshotTests: XCTestCase {
    /// Первый запуск на свежем симуляторе CI бывает долгим.
    private static let launchTimeout: TimeInterval = 30
    private static let screenTimeout: TimeInterval = 10

    @MainActor
    func test01RootAndInstallCheck() {
        let app = launchApp()
        let opened = rootShown(in: app)
        snapshot("01-root")
        XCTAssertTrue(opened, "Стартовый экран не открылся")

        // «Проверка установки» встроена в стартовый экран ниже заголовка — второй снимок после прокрутки до кнопки
        // «Лаборатории» в конце списка. Первая прокрутка — всегда: на высоком экране кнопка видна и без неё.
        app.swipeUp()
        let reached = reveal(button(in: app, containing: "Лаборатория"), in: app)
        snapshot("02-install-check")
        XCTAssertTrue(reached, "Не видно кнопки «Лаборатория» внизу стартового экрана")
    }

    @MainActor
    func test02Lab() {
        let app = launchApp()
        let opened = openLab(in: app)
        snapshot("03-lab")
        XCTAssertTrue(opened, "«Лаборатория» не открылась")
    }

    @MainActor
    func test03WalkLab() {
        openLabScreen(link: "Прогулка:", title: "Прогулка", snapshot: "04-lab-walk")
    }

    @MainActor
    func test04MapStress() {
        // Тайлы Apple Maps и туман дорисовываются уже после открытия экрана.
        openLabScreen(link: "Карта:", title: "Карта", snapshot: "05-lab-map", settle: 6)
    }

    /// Сборка снимков знает адрес сервера (зарезервированный домен `.invalid`, ios-snapshots.yml), но вход
    /// не выполнен. У пробной установки (ipa.yml) адреса нет вовсе, поэтому строка «Адрес» у неё другая: «не задан»
    /// вместо gorodki.invalid. Раздел «Реальное время» (спайк S7) с адресом и без входа показывает «нужен вход» —
    /// когда он появится на этом экране, проверить здесь и его.
    @MainActor
    func test05ServerLab() {
        let app = openLabScreen(link: "Сервер:", title: "Сервер", snapshot: nil)
        let notSignedIn = waitForAny([text(in: app, containing: "не выполнен")], timeout: Self.screenTimeout)
        snapshot("06-lab-server")
        XCTAssertTrue(notSignedIn, "На экране сервера нет строки «Вход: не выполнен»")
    }

    /// Пробный забег без входа и сервера: снимок до «Начать» — «Старт» на симуляторе спросил бы разрешения.
    @MainActor
    func test06ProbeRun() {
        openLabScreen(link: "Пробный забег", title: "Пробный забег", snapshot: "07-lab-probe-run")
    }

    // MARK: - Переходы

    @MainActor
    private func launchApp() -> XCUIApplication {
        let app = XCUIApplication()
        // Язык и регион — как на телефоне игрока: даты и системные кнопки («Назад») на снимках по-русски.
        app.launchArguments += ["-AppleLanguages", "(ru)", "-AppleLocale", "ru_RU"]
        app.launch()
        return app
    }

    @MainActor
    private func openLab(in app: XCUIApplication) -> Bool {
        let link = button(in: app, containing: "Лаборатория")
        guard rootShown(in: app), reveal(link, in: app) else { return false }
        link.tap()
        return screenShown(in: app, titled: "Лаборатория")
    }

    @MainActor
    private func rootShown(in app: XCUIApplication) -> Bool {
        waitForAny(
            [app.staticTexts["Городки"].firstMatch, text(in: app, containing: "Обеги участок")],
            timeout: Self.launchTimeout)
    }

    /// Экран открыт, когда на панели навигации его заголовок: панель названа им, или на ней такой текст.
    @MainActor
    private func screenShown(in app: XCUIApplication, titled title: String) -> Bool {
        let startsWithTitle = NSPredicate(format: "identifier BEGINSWITH %@ OR label BEGINSWITH %@", title, title)
        return waitForAny(
            [
                app.navigationBars.matching(startsWithTitle).firstMatch,
                app.navigationBars.staticTexts.matching(startsWithTitle).firstMatch,
            ],
            timeout: Self.screenTimeout)
    }

    /// Открыть экран «Лаборатории» и снять его: `settle` — сколько секунд дать экрану дорисоваться до снимка.
    @discardableResult
    @MainActor
    private func openLabScreen(
        link: String, title: String, snapshot name: String?, settle: TimeInterval = 1
    ) -> XCUIApplication {
        let app = launchApp()
        let labOpened = openLab(in: app)
        let entry = button(in: app, containing: link)
        let reached = labOpened && reveal(entry, in: app)
        if reached {
            entry.tap()
        }
        let opened = reached && screenShown(in: app, titled: title)
        pause(settle)
        if let name {
            snapshot(name)
        }
        XCTAssertTrue(labOpened, "«Лаборатория» не открылась")
        XCTAssertTrue(reached, "В «Лаборатории» нет кнопки «\(link)…»")
        XCTAssertTrue(opened, "Экран «\(title)…» не открылся")
        return app
    }

    /// Прокрутить список, пока элемент не станет доступен для нажатия: строки списка создаются по мере прокрутки.
    @MainActor
    private func reveal(_ element: XCUIElement, in app: XCUIApplication) -> Bool {
        for _ in 0..<6 {
            if element.exists, element.isHittable {
                return true
            }
            app.swipeUp()
        }
        return element.exists && element.isHittable
    }

    // MARK: - Поиск элементов

    /// Строка списка с переходом (`NavigationLink`): SwiftUI показывает её кнопкой, но бывает и ячейкой.
    @MainActor
    private func button(in app: XCUIApplication, containing label: String) -> XCUIElement {
        let rowOrButton = NSPredicate(
            format: "(elementType == %lu OR elementType == %lu) AND label CONTAINS %@",
            XCUIElement.ElementType.button.rawValue, XCUIElement.ElementType.cell.rawValue, label)
        return app.descendants(matching: .any).matching(rowOrButton).firstMatch
    }

    /// Строка списка вида «Вход — не выполнен» бывает одним элементом (текст — в `value`) или двумя (в `label`).
    @MainActor
    private func text(in app: XCUIApplication, containing fragment: String) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "label CONTAINS %@ OR value CONTAINS %@", fragment, fragment))
            .firstMatch
    }

    /// Дождаться, пока появится хотя бы один из элементов.
    @MainActor
    private func waitForAny(_ elements: [XCUIElement], timeout: TimeInterval) -> Bool {
        let deadline = Date.now.addingTimeInterval(timeout)
        repeat {
            if elements.contains(where: { $0.exists }) {
                return true
            }
            pause(0.5)
        } while Date.now < deadline
        return elements.contains(where: { $0.exists })
    }

    // MARK: - Снимки

    /// Снимок всего экрана со строкой состояния, как у снимка на телефоне. Имя — латиницей: из него workflow делает
    /// имя PNG-файла, а кириллица в именах файлов из архива артефакта на Windows не везде читается.
    @MainActor
    private func snapshot(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    /// Подождать, ничего не проверяя: у экрана нет признака «дорисовался», а снимок нужен уже с картой.
    @MainActor
    private func pause(_ seconds: TimeInterval) {
        _ = XCTWaiter.wait(for: [XCTestExpectation(description: "экран дорисовывается")], timeout: seconds)
    }
}
