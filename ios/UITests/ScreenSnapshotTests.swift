import XCTest

/// Снимки экранов, которые открываются без сервера (PLAN.md, §8 и §13: «снимки и UI-тесты — информационно»):
/// отладочное меню с «Проверкой установки» и «Лаборатория» — прогулка (S1), стенд карты (S4), сервер (S7), пробный
/// забег, дизайн в обеих темах; онбординг, вкладки и карта (земля, лист участка, туман, подсказка разрешения) —
/// в режиме фикстур (`-GorodkiScreen`, `-GorodkiFixture`, `-GorodkiTheme`; App/Fixtures), каждый экран днём и ночью. Идут в ios-snapshots.yml на симуляторе iPhone; снимки — вложения XCTest, workflow
/// выгружает их из пакета результатов в PNG (артефакт запуска): так приложение можно посмотреть без Mac.
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

    /// Отладочное меню (`-GorodkiScreen debug`) — то, что раньше было стартовым экраном: «Лаборатория» сверху,
    /// «Проверка установки» ниже.
    @MainActor
    func test01DebugMenuAndInstallCheck() {
        let app = launchApp()
        let opened = rootShown(in: app)
        snapshot("01-debug-menu")
        XCTAssertTrue(opened, "Отладочное меню не открылось")

        // Второй снимок — после прокрутки до кнопки «Проверить заново» в конце списка. Первая прокрутка — всегда:
        // на высоком экране кнопка видна и без неё.
        app.swipeUp()
        let reached = reveal(button(in: app, containing: "Проверить заново"), in: app)
        snapshot("02-install-check")
        XCTAssertTrue(reached, "Не видно кнопки «Проверить заново» внизу отладочного меню")
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

    /// «Лаборатория → Дизайн» (design/APPROVALS.md): вся страница днём — по снимку на экран прокрутки. Церемония
    /// захвата играет сама, когда появляется, поэтому перед снимком — пауза дольше церемонии (1,9 с).
    @MainActor
    func test07DesignLabDay() {
        let app = openLabScreen(link: "Дизайн:", title: "Дизайн", snapshot: nil, settle: 3)
        snapshotDesignLab(in: app, prefix: "08-design-day")
    }

    /// То же ночью: переключатель темы — только в «Лаборатории», у игры своего нет.
    @MainActor
    func test08DesignLabNight() {
        let app = openLabScreen(link: "Дизайн:", title: "Дизайн", snapshot: nil, settle: 1)
        let night = app.buttons["Ночь"].firstMatch
        let switched = night.waitForExistence(timeout: Self.screenTimeout)
        if switched {
            night.tap()
        }
        pause(3)
        snapshotDesignLab(in: app, prefix: "09-design-night")
        XCTAssertTrue(switched, "На экране «Дизайн» нет переключателя «Ночь»")
    }

    // MARK: - Онбординг и вкладки (режим фикстур)

    @MainActor
    func test10OnboardingIntro() {
        snapshotScreen("intro", expecting: "Обеги — и участок твой", name: "10-onboarding-intro")
    }

    @MainActor
    func test11Invite() {
        snapshotScreen("invite", expecting: "Код приглашения", name: "11-onboarding-invite")
    }

    /// Ошибка входа «код не подошёл» — на поле кода, текстом `SignInFailure`.
    @MainActor
    func test12InviteInvalid() {
        snapshotScreen(
            "invite", fixture: "invite-invalid", expecting: "Код приглашения не подошёл",
            name: "12-onboarding-invite-invalid")
    }

    @MainActor
    func test13Age() {
        snapshotScreen("age", expecting: "Тебе есть 16?", name: "13-onboarding-age")
    }

    /// Согласие длинное — снимок сверху и после прокрутки.
    @MainActor
    func test14Consent() {
        snapshotScreen("consent", expecting: "Правила и согласие", name: "14-onboarding-consent", pages: 3)
    }

    /// Без Client ID Google кнопка входа выключена и объясняет почему.
    @MainActor
    func test15SignIn() {
        snapshotScreen(
            "sign-in", expecting: "Вход через Google появится после настройки", name: "15-onboarding-sign-in")
    }

    @MainActor
    func test16SignInOffline() {
        snapshotScreen(
            "sign-in", fixture: "offline", expecting: "Нет связи с сервером", name: "16-onboarding-sign-in-offline")
    }

    /// Тайлы Apple Maps дорисовываются уже после открытия экрана.
    @MainActor
    func test17Map() {
        snapshotScreen("map", fixture: "player", expecting: "Старт", name: "17-tab-map", settle: 6)
    }

    @MainActor
    func test18Leaderboards() {
        snapshotScreen("leaderboards", expecting: "Рейтинги — скоро", name: "18-tab-leaderboards")
    }

    @MainActor
    func test19Clan() {
        snapshotScreen("clan", expecting: "Кланы — скоро", name: "19-tab-clan")
    }

    @MainActor
    func test20Profile() {
        snapshotScreen("profile", fixture: "player", expecting: "Бегун-1234", name: "20-tab-profile")
    }

    /// Вход через Google настроен: кнопка активна, пояснения «появится после настройки» нет.
    @MainActor
    func test21SignInGoogleReady() {
        snapshotScreen(
            "sign-in", fixture: "google-ready", expecting: "Войти через Google", name: "21-onboarding-sign-in-ready")
    }

    /// Остальные ошибки входа — та же карточка, что у «нет связи» (16), с другим текстом: только днём.
    @MainActor
    func test22SignInErrors() {
        snapshotScreen(
            "sign-in", fixture: "google-rejected", expecting: "Google не подтвердил вход",
            name: "22-onboarding-sign-in-google-rejected", themes: ["day"])
        snapshotScreen(
            "sign-in", fixture: "account-deleting", expecting: "Этот аккаунт удаляется",
            name: "23-onboarding-sign-in-account-deleting", themes: ["day"])
    }

    // MARK: - Карта (MapFixture: образцы territory.json и fog.json и земля вокруг)

    /// «Захват»: земли по отношению и уровню, кромки, муравьи на спорном, «Старт». Тайлы Apple Maps дорисовываются
    /// уже после открытия экрана.
    @MainActor
    func test23MapCapture() {
        snapshotScreen("map", fixture: "player-map", expecting: "Отношения", name: "24-map-capture", settle: 6)
    }

    /// Лист участка по касанию: свой кусок образца, щит и живая зона «спорная».
    @MainActor
    func test24MapParcel() {
        snapshotScreen("map-parcel", expecting: "Твоя земля", name: "25-map-parcel", settle: 6)
    }

    /// «Исследование»: туман с кромкой открытого, земли скрыты, сводка тумана.
    @MainActor
    func test25MapExplore() {
        snapshotScreen("map-explore", expecting: "Открыто", name: "26-map-explore", settle: 6)
    }

    /// «Старт» без разрешений — подсказка перед системным запросом геопозиции (системного окна нет: фикстура).
    @MainActor
    func test26MapStartPrimer() {
        snapshotScreen(
            "map-start", expecting: "Геопозиция — на время забега", name: "27-map-start-location", settle: 2,
            themes: ["day"])
    }

    /// Режим «Отношения»: моё — своим цветом, соперники — красным. Переключатель — нажатием, только днём.
    @MainActor
    func test27MapRelations() {
        let app = launchApp(["-GorodkiScreen", "map", "-GorodkiFixture", "player-map", "-GorodkiTheme", "day"])
        let segment = app.buttons["Отношения"].firstMatch
        let shown = segment.waitForExistence(timeout: Self.launchTimeout)
        if shown {
            segment.tap()
        }
        pause(6)
        snapshot("28-map-relations-day")
        XCTAssertTrue(shown, "На карте нет переключателя «Отношения»")
    }

    /// Экран режима фикстур днём и ночью (`themes`): по запуску на тему, снимок — до проверки текста, как и у
    /// остальных. `pages` — сколько снимков с прокруткой между ними.
    @MainActor
    private func snapshotScreen(
        _ screen: String, fixture: String? = nil, expecting fragment: String, name: String,
        settle: TimeInterval = 1, pages: Int = 1, themes: [String] = ["day", "night"]
    ) {
        for theme in themes {
            var arguments = ["-GorodkiScreen", screen, "-GorodkiTheme", theme]
            if let fixture {
                arguments += ["-GorodkiFixture", fixture]
            }
            let app = launchApp(arguments)
            let shown = waitForAny([text(in: app, containing: fragment)], timeout: Self.launchTimeout)
            pause(settle)
            snapshot("\(name)-\(theme)")
            for page in stride(from: 2, through: pages, by: 1) {
                app.swipeUp()
                pause(1.5)
                snapshot("\(name)-\(theme)-\(page)")
            }
            XCTAssertTrue(shown, "«\(screen)» (\(theme)): нет текста «\(fragment)»")
            app.terminate()
        }
    }

    @MainActor
    private func snapshotDesignLab(in app: XCUIApplication, prefix: String) {
        snapshot("\(prefix)-1")
        for page in 2...7 {
            app.swipeUp()
            pause(2.5)
            snapshot("\(prefix)-\(page)")
        }
    }

    // MARK: - Переходы

    /// Запуск; без аргументов — сразу отладочное меню: «Проверка установки» и «Лаборатория» живут теперь там,
    /// а корень приложения без входа — онбординг.
    @MainActor
    private func launchApp(_ arguments: [String] = ["-GorodkiScreen", "debug"]) -> XCUIApplication {
        let app = XCUIApplication()
        // Язык и регион — как на телефоне игрока: даты и системные кнопки («Назад») на снимках по-русски.
        app.launchArguments += ["-AppleLanguages", "(ru)", "-AppleLocale", "ru_RU"] + arguments
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
            [text(in: app, containing: "Проверка установки"), button(in: app, containing: "Лаборатория")],
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
