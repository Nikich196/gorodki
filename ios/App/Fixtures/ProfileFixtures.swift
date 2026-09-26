#if DEBUG
    import Foundation
    import GameCore
    import GorodkiAPI
    import Networking
    import SwiftUI
    import Sync

    /// Экраны профиля и настроек в режиме фикстур (`-GorodkiScreen settings`, `privacy-zones`, `home`,
    /// `exploration-stats`, `offline`, `map-home`): данные — образцы `contracts/samples` и простые значения, сервера нет.
    /// Имена `-GorodkiFixture`: `empty` — зон нет; `no-osm` — у сервера нет набора OSM, «% Бреста» и районов нет;
    /// `offline`, `server-down` — статистика не загрузилась.
    struct ProfileFixtureScreen: View {
        let screen: FixtureScreen
        let fixture: String?

        var body: some View {
            switch screen {
            case .settings:
                NavigationStack {
                    SettingsView(
                        model: SettingsModel(
                            profile: Fixtures.profile("player"), home: HomeModel(home: ProfileFixtures.home),
                            account: FixtureSettingsAccount()))
                }
            case .privacyZones:
                NavigationStack {
                    PrivacyZonesView(
                        model: PrivacyZonesModel(
                            service: FixtureZones(),
                            zones: fixture == "empty" ? [] : ProfileFixtures.zones))
                }
            case .home:
                NavigationStack {
                    HomeView(model: HomeModel(home: ProfileFixtures.home))
                }
            default:
                NavigationStack {
                    ExplorationStatsView(model: ProfileFixtures.stats(fixture))
                }
            }
        }
    }

    @MainActor
    enum ProfileFixtures {
        /// «Дом» у восточного края окна карты фикстуры: на снимке виден и круг, и его край.
        static var home: Coordinate {
            let window = MapFixture.window
            return Coordinate(latitude: (window.south + window.north) / 2 - 0.0012, longitude: window.east - 0.0035)
        }

        /// Две зоны у центра Бреста; радиус и время — как ответил бы сервер.
        nonisolated static let zones = [
            PrivacyZone(
                id: "0199a1b2-0000-7000-8000-00000000a001", center: Coordinate(latitude: 52.0935, longitude: 23.6930),
                radiusMeters: 400, createdAtMs: 1_790_000_000_000),
            PrivacyZone(
                id: "0199a1b2-0000-7000-8000-00000000a002", center: Coordinate(latitude: 52.1040, longitude: 23.7160),
                radiusMeters: 400, createdAtMs: 1_790_300_000_000),
        ]

        /// Статистика из образцов `fog-summary.json` (с «% Бреста» и районами, контракт E9) и `seasons.json`; `no-osm` —
        /// те же числа без процентов, `offline` и `server-down` — ошибка загрузки.
        static func stats(_ fixture: String?) -> ExplorationStatsModel {
            switch fixture {
            case "offline": return ExplorationStatsModel(api: nil, failure: .offline)
            case "server-down": return ExplorationStatsModel(api: nil, failure: .serverUnavailable)
            default: break
            }
            guard let fog = Fixtures.sample("fog-summary", as: Components.Schemas.FogSummaryResponse.self) else {
                return ExplorationStatsModel(api: nil)
            }
            var summary = ExplorationSummary(
                summary: fog, seasons: Fixtures.sample("seasons", as: Components.Schemas.SeasonsResponse.self))
            if fixture == "no-osm" {
                summary.allTime.brestPercent = nil
                summary.allTime.districts = nil
                for index in summary.seasons.indices {
                    summary.seasons[index].brestPercent = nil
                    summary.seasons[index].districts = nil
                }
            }
            return ExplorationStatsModel(api: nil, summary: summary)
        }

        /// Профиль без сети: ник ещё не пришёл, причина — «нет сети».
        static func offlineProfile() -> ProfileModel {
            let profile = ProfileModel(signedIn: true)
            profile.loadFailure = .offline
            return profile
        }
    }

    /// Сервер для фикстур: всё удаётся сразу, ничего не хранит.
    @MainActor
    struct FixtureSettingsAccount: SettingsAccount {
        func setPublicProfile(_ enabled: Bool) async throws -> Bool { enabled }
        func clearExplorationHistory() async throws {}
        func deleteAccount() async throws -> Int64? { nil }
        func signOut() async throws {}
    }

    /// Зоны для фикстур: добавление удаётся сразу.
    struct FixtureZones: PrivacyZoneService {
        func zones() async throws -> [PrivacyZone] { ProfileFixtures.zones }
        func add(at center: Coordinate) async throws -> PrivacyZone {
            PrivacyZone(id: UUID().uuidString, center: center, radiusMeters: 400, createdAtMs: 0)
        }
        func remove(id: String) async throws {}
    }
#endif
