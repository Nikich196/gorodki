import DesignSystem
import Foundation
import SwiftUI

/// Режим фикстур для снимков экранов (ios/UITests, docs/architecture/ios-app.md, «Режим фикстур»): аргументы запуска
/// открывают экран сразу, с данными из образцов `contracts/samples` и в нужной теме — без сервера, входа и нажатий.
///
///     -GorodkiScreen profile -GorodkiFixture player -GorodkiTheme night
///
/// Работает только в Debug: в Release `current` всегда пустой, а образцов в сборке нет (`project.yml`).
struct LaunchOptions: Equatable {
    var screen: FixtureScreen?
    var fixture: String?
    var theme: Theme?

    static let screenArgument = "-GorodkiScreen"
    static let fixtureArgument = "-GorodkiFixture"
    static let themeArgument = "-GorodkiTheme"

    /// Аргументы этого запуска.
    static let current: LaunchOptions = {
        #if DEBUG
            return parse(ProcessInfo.processInfo.arguments)
        #else
            return LaunchOptions()
        #endif
    }()

    /// Разобрать аргументы запуска: значение — следующий аргумент. Неизвестный экран или тема — как не заданные.
    static func parse(_ arguments: [String]) -> LaunchOptions {
        func value(after name: String) -> String? {
            guard let index = arguments.firstIndex(of: name), arguments.indices.contains(index + 1) else {
                return nil
            }
            return arguments[index + 1]
        }
        var options = LaunchOptions()
        options.screen = value(after: screenArgument).flatMap(FixtureScreen.init(rawValue:))
        options.fixture = value(after: fixtureArgument)
        options.theme =
            switch value(after: themeArgument) {
            case "day": .day
            case "night": .night
            default: nil
            }
        return options
    }

    /// Тема для `.preferredColorScheme`: `nil` — как в системе.
    var colorScheme: ColorScheme? {
        theme.map { $0 == .night ? .dark : .light }
    }
}

/// Экраны, которые режим фикстур открывает сразу (`-GorodkiScreen`).
enum FixtureScreen: String, CaseIterable, Sendable {
    // Онбординг.
    case intro, invite, age, terms, consent, signIn = "sign-in"
    // Вкладки.
    case map, leaderboards, clan, profile
    /// Отладочное меню: «Проверка установки» и «Лаборатория».
    case debug
    /// Экраны пунктов листика из «Профиля» (`ProfileFeature`) и «Отладка → Пункты задания».
    case gallery, replay, storage, backup, files, calendar, inviteFriend = "invite-friend"
    case myQR = "my-qr", scanner, notifications, assignment
}
