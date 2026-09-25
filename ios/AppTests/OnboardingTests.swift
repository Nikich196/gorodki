import DesignSystem
import Foundation
import GorodkiAPI
import Networking
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

    @Test("«Уже играю»: вход без регистрации; аккаунта нет — к коду приглашения с пояснением")
    func returningPlayer() async {
        let model = OnboardingModel(
            signIn: { _, registration in registration == nil ? .registrationNeeded : .signedIn(isNewUser: true) },
            googleToken: { "id-token" })
        model.startReturning()
        #expect(model.path == [.signIn])
        #expect(model.registration == nil)
        await model.signInWithGoogle()
        #expect(model.path == [.invite])
        #expect(!model.returningPlayer)
        #expect(model.errorMessage != nil)
    }

    @Test("Ошибка входа — текстом SignInService; неверный код возвращает к полю кода")
    func failures() {
        let model = OnboardingModel(signIn: nil, googleToken: nil)
        model.path = [.invite, .age, .consent, .signIn]
        model.apply(.failed(.offline))
        #expect(model.errorMessage == SignInFailure.offline.message)
        #expect(model.path.last == .signIn)
        model.apply(.failed(.inviteInvalid))
        #expect(model.errorMessage == SignInFailure.inviteInvalid.message)
        #expect(model.path == [.invite])
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

    @Test("Отладочное меню: роли demo и admin — всегда; сборка команды (Debug) — всем")
    func debugAccess() {
        #expect(DebugAccess.isAvailable(role: "admin"))
        #expect(DebugAccess.isAvailable(role: "demo"))
        #expect(DebugAccess.isAvailable(role: "player") == DebugAccess.buildAllows)
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
        #expect(LegalDocument.load(.terms) != nil)
        #expect(LegalDocument.load(.privacy) != nil)
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
