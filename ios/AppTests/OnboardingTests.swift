import DesignSystem
import Foundation
import GorodkiAPI
import Networking
import Sync
import Testing

@testable import Gorodki

/// Онбординг, режим фикстур и отладочное меню (docs/architecture/ios-app.md, «Оболочка и онбординг»).
@Suite("Онбординг: код, отметки, вход, фикстуры")
@MainActor
struct OnboardingTests {
    @Test("Код приглашения — без пробелов по краям и заглавными")
    func inviteCode() {
        #expect(InviteCode.normalized("  abcd-2345 \n") == "ABCD-2345")
        #expect(InviteCode.normalized("   ").isEmpty)
    }

    @Test("Путь нового игрока: код → 16+ → согласие → вход; серверу уходят нормализованный код и обе отметки")
    func registrationPath() {
        let model = OnboardingModel(signIn: nil, googleToken: nil)
        model.advance(from: .intro)
        #expect(model.path == [.invite])
        #expect(!model.inviteReady)
        model.inviteCode = " abcd-2345 "
        model.advance(from: .invite)
        #expect(model.inviteCode == "ABCD-2345")
        model.ageConfirmed = true
        model.advance(from: .age)
        model.termsAccepted = true
        #expect(!model.consentReady, "Соглашение без согласия — ещё не всё")
        model.consentGiven = true
        model.advance(from: .consent)
        #expect(model.path == [.invite, .age, .consent, .signIn])
        #expect(
            model.registration == Registration(inviteCode: "ABCD-2345", ageConfirmed: true, consentAccepted: true))
        #expect(!model.signInAvailable, "Без Client ID и сервера кнопка Google выключена")
    }

    @Test("«Уже играю», а аккаунта нет — к коду с пояснением; потом вход с тем же токеном Google, без второго окна")
    func returningPlayer() async {
        let googleCalls = Calls()
        let model = OnboardingModel(
            signIn: { _, registration in registration == nil ? .registrationNeeded : .signedIn(isNewUser: true) },
            googleToken: {
                await googleCalls.add()
                return "id-token"
            })
        model.startReturning()
        #expect(model.path == [.signIn])
        #expect(model.registration == nil)
        await model.signInWithGoogle()
        #expect(model.path == [.invite])
        #expect(!model.returningPlayer)
        #expect(model.errorMessage(on: .invite) != nil)
        #expect(model.errorMessage(on: .signIn) == nil, "Ошибка — только на своём шаге")

        model.inviteCode = "abcd-2345"
        model.advance(from: .invite)
        model.ageConfirmed = true
        model.advance(from: .age)
        model.termsAccepted = true
        model.consentGiven = true
        model.advance(from: .consent)
        await model.signInWithGoogle()
        #expect(await googleCalls.count == 1)
        #expect(model.error == nil)
    }

    @Test("Ошибка входа — текстом SignInService и на своём шаге: код, 16+ и согласие возвращают к своим шагам")
    func failures() {
        let model = OnboardingModel(signIn: nil, googleToken: nil)
        let all: [OnboardingStep] = [.invite, .age, .consent, .signIn]
        model.path = all
        model.apply(.failed(.offline))
        #expect(model.errorMessage(on: .signIn) == SignInFailure.offline.message)
        #expect(model.path == all)
        model.apply(.failed(.consentOutdated))
        #expect(model.path == all, "Устарело соглашение — нужно обновить приложение, а не отметку")
        model.apply(.failed(.consentRequired))
        #expect(model.path == [.invite, .age, .consent])
        #expect(model.errorMessage(on: .consent) == SignInFailure.consentRequired.message)
        model.path = all
        model.apply(.failed(.ageNotConfirmed))
        #expect(model.path == [.invite, .age])
        #expect(model.errorMessage(on: .age) == SignInFailure.ageNotConfirmed.message)
        #expect(model.errorMessage(on: .signIn) == nil)
        model.path = all
        model.apply(.failed(.inviteInvalid))
        #expect(model.errorMessage(on: .invite) == SignInFailure.inviteInvalid.message)
        #expect(model.path == [.invite])
        model.advance(from: .invite)
        #expect(model.error == nil, "Ушёл с шага — ошибки нет")
    }

    @Test("Аргументы запуска: экран, фикстура и тема; неизвестное — как не заданное")
    func launchOptions() {
        let options = LaunchOptions.parse([
            "Gorodki", "-GorodkiScreen", "sign-in", "-GorodkiFixture", "offline", "-GorodkiTheme", "night",
        ])
        #expect(options.screen == .signIn)
        #expect(options.fixture == "offline")
        #expect(options.theme == .night)
        #expect(options.colorScheme == .dark)
        #expect(LaunchOptions.parse(["Gorodki", "-GorodkiScreen", "нет-такого", "-GorodkiTheme"]) == LaunchOptions())
    }

    @Test("Фикстура player — образцы contracts/samples: ник, цвет, туман и сезон")
    func playerFixture() {
        let profile = Fixtures.profile("player")
        #expect(profile.signedIn)
        #expect(profile.displayName == "Бегун-1234")
        #expect(profile.playerColor == PlayerColor(index: 7))
        #expect(profile.exploredSquareMeters == 42_580.5)
        #expect(profile.seasonExploredSquareMeters == 11_074.9)
        #expect(profile.seasonName == "Сезон 0 (бета)")
    }

    @Test("Цвет по номеру с сервера — по порядку палитры, номер вне 0–11 — по модулю")
    func playerColorIndex() {
        #expect(PlayerColor(index: 0) == PlayerColor.allCases[0])
        #expect(PlayerColor(index: 11) == PlayerColor.allCases[11])
        #expect(PlayerColor(index: 12) == PlayerColor.allCases[0])
        #expect(PlayerColor(index: -1) == PlayerColor.allCases[11])
    }

    @Test("Отладочное меню: роли demo и admin — всегда; игрок — только в сборке команды (Debug, GORODKI_PROBE)")
    func debugAccess() {
        #expect(!DebugAccess.isAvailable(role: "player", buildAllows: false), "IPA для игроков — без меню")
        #expect(!DebugAccess.isAvailable(role: nil, buildAllows: false))
        #expect(DebugAccess.isAvailable(role: "admin", buildAllows: false))
        #expect(DebugAccess.isAvailable(role: "demo", buildAllows: false))
        #expect(DebugAccess.isAvailable(role: nil, buildAllows: true))
        #expect(DebugAccess.isAvailable(role: "player", buildAllows: true))
    }

    @Test("Выход: пробный забег мешает — просим закончить; вход стёрт, а данные нет — сообщение корню")
    func signOutFailures() {
        let probe = ProfileTab.signOutFailure(TrackerError.alreadyRunning, signedOut: false)
        #expect(probe.stayedSignedIn)
        #expect(probe.text.contains("Лаборатории"))
        let finish = ProfileTab.signOutFailure(CocoaError(.fileWriteUnknown), signedOut: false)
        #expect(finish.stayedSignedIn)
        #expect(!finish.text.contains("operation"), "Без английского localizedDescription")
        let wipe = ProfileTab.signOutFailure(CocoaError(.fileWriteUnknown), signedOut: true)
        #expect(!wipe.stayedSignedIn)
    }

    @Test("Согласие v1 в ресурсах: черновик виден, отметку экран рисует сам, строки таблицы разобраны")
    func consentDocument() throws {
        let consent = try #require(LegalDocument.load(.consent))
        #expect(consent.blocks.first == .title("Согласие на обработку персональных данных «Городков» — версия 1"))
        let quote = consent.blocks.contains { block in
            if case .quote(let lines) = block { return lines.first?.contains("ЧЕРНОВИК") == true }
            return false
        }
        #expect(quote, "Плашка «ЧЕРНОВИК» — как в документе")
        let rows = consent.blocks.compactMap { block -> [String]? in
            if case .row(let cells, _) = block { return cells }
            return nil
        }
        #expect(rows.first?.first == "Оператор")
        #expect(rows.count == 8)
        #expect(!consent.blocks.contains(.heading("Отметка")))
        #expect(consent.checkbox?.hasPrefix("Даю согласие на обработку моих персональных данных") == true)
        // Файлы той версии, которую приложение отправляет серверу (`SignInService.consentVersion`), — в ресурсах.
        for name in LegalDocument.Name.allCases {
            #expect(LegalDocument.load(name) != nil, "Нет \(name.fileName).md в сборке")
        }
    }

    @Test("Подпись отметки — строка «☐ …» из раздела отметки; раздел не попадает в текст")
    func checkboxLine() {
        let document = LegalDocument.parse(
            """
            Текст.

            ## Отметка

            ☐ Даю согласие.
            """, stopAt: "## Отметка")
        #expect(document.blocks == [.paragraph("Текст.")])
        #expect(document.checkbox == "Даю согласие.")
        #expect(LegalDocument.parse("Текст.").checkbox == nil)
    }

    @Test("Разбор Markdown: заголовки, пункты с продолжением, ссылки между документами")
    func markdown() throws {
        let document = LegalDocument.parse(
            """
            # Заголовок

            Абзац
            в две строки.

            - пункт
              с продолжением
              - вложенный
            """)
        #expect(
            document.blocks == [
                .title("Заголовок"), .paragraph("Абзац в две строки."), .bullet("пункт с продолжением", level: 0),
                .bullet("вложенный", level: 1),
            ])
        #expect(LegalDocument.Name(link: try #require(URL(string: "privacy-v1.md"))) == .privacy)
        #expect(LegalDocument.Name(link: try #require(URL(string: "README.md"))) == nil)
    }
}

/// Сколько раз открывалось окно Google.
private actor Calls {
    private(set) var count = 0

    func add() {
        count += 1
    }
}
