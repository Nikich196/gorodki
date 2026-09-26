import Foundation
import GameCore
import GorodkiAPI
import Networking
import Persistence
import Sync
import Testing

@testable import Gorodki

/// Профиль, настройки, приватные зоны, «Дом», статистика и кнопки карты (docs/architecture/ios-app.md, «Профиль
/// и настройки»): модели с простыми значениями и подменой сервера.
@Suite("Профиль и настройки: согласие на ник, очистка тумана, зоны, «Дом», «Где я»")
@MainActor
struct ProfileSettingsTests {
    /// Сервер «Настроек»: отвечает заданным или бросает ошибку; считает вызовы.
    final class FakeAccount: SettingsAccount {
        var publicProfileResult: Result<Bool, any Error> = .success(true)
        var clearError: (any Error)?
        var deleteResult: Result<Int64?, any Error> = .success(nil)
        var clears = 0

        func setPublicProfile(_ enabled: Bool) async throws -> Bool { try publicProfileResult.get() }
        func clearExplorationHistory() async throws {
            clears += 1
            if let clearError { throw clearError }
        }
        func deleteAccount() async throws -> Int64? { try deleteResult.get() }
        func signOut() async throws {}
    }

    /// Зоны: в памяти, с заданной ошибкой.
    actor FakeZones: PrivacyZoneService {
        var stored: [PrivacyZone] = []
        var failure: (any Error)?

        func fail(_ error: (any Error)?) { failure = error }

        func zones() async throws -> [PrivacyZone] {
            if let failure { throw failure }
            return stored
        }

        func add(at center: Coordinate) async throws -> PrivacyZone {
            if let failure { throw failure }
            let zone = PrivacyZone(
                id: "zone-\(stored.count)", center: center, radiusMeters: 400, createdAtMs: Int64(stored.count))
            stored.append(zone)
            return zone
        }

        func remove(id: String) async throws {
            if let failure { throw failure }
            stored.removeAll { $0.id == id }
        }
    }

    private static let brest = Coordinate(latitude: 52.0976, longitude: 23.7341)

    private func settings(_ account: FakeAccount) -> SettingsModel {
        let profile = ProfileModel(signedIn: true)
        profile.publicProfile = false
        return SettingsModel(profile: profile, home: HomeModel(), account: account, now: { 1_790_348_400_000 })
    }

    // MARK: - Настройки

    @Test("Согласие на ник: сервер записал — в профиле; заглушка сервера (500) — отметка обратно и честная причина")
    func publicProfile() async {
        let account = FakeAccount()
        let model = settings(account)
        await model.setPublicProfile(true)
        #expect(model.profile.publicProfile == true)
        #expect(model.publicProfileError == nil)

        account.publicProfileResult = .failure(AccountServiceError.unexpectedStatus(500))
        await model.setPublicProfile(false)
        #expect(model.profile.publicProfile == true)
        #expect(model.publicProfileError == .serverError(status: 500))
        #expect(model.publicProfileError?.message.contains("(500)") == true)
    }

    @Test("Очистка истории: удалась — карта и профиль узнают; туман занят — причина, повторить можно")
    func clearExploration() async {
        let account = FakeAccount()
        let model = settings(account)
        var cleared = 0
        model.onFogCleared = { cleared += 1 }

        await model.clearExplorationHistory()
        #expect(model.clearResult == .done && cleared == 1)

        account.clearError = AccountServiceError.rejected(status: 503, code: "fog_clear_busy")
        await model.clearExplorationHistory()
        #expect(model.clearResult == .failed(.rejected(status: 503, code: "fog_clear_busy")))
        #expect(cleared == 1 && account.clears == 2)
    }

    @Test(
        "Без сервера — кнопки сервера выключены; удаление: срок стирания от сервера текстом, отказ — ничего не стёрто")
    func deletion() async {
        #expect(!SettingsModel(profile: ProfileModel(signedIn: true), home: HomeModel(), account: nil).serverReady)
        #expect(settings(FakeAccount()).serverReady)

        let minsk = TimeZone(identifier: "Europe/Minsk") ?? .current
        let notice = SettingsModel.deletedNotice(
            deleteByMs: 1_790_348_400_000 + 15 * 86_400_000, now: 1_790_348_400_000, timeZone: minsk)
        #expect(notice.contains("до 10 октября, 18:00"))
        #expect(SettingsModel.deletedNotice(deleteByMs: nil, now: 0).contains("аккаунта уже нет"))
        #expect(SettingsModel.deleteFailure(URLError(.notConnectedToInternet)).contains("ничего не стёрто"))
        #expect(SettingsModel.deleteFailure(TrackerError.alreadyRunning).contains("пробный забег"))

        let account = FakeAccount()
        account.deleteResult = .failure(URLError(.timedOut))
        let model = settings(account)
        await model.deleteAccount()
        #expect(model.deleteError?.contains("Сервер не отвечает") == true)
    }

    // MARK: - Приватные зоны

    @Test("Зоны: загрузка, добавление, удаление; предел — «Добавить» выключена; отказ сервера — под списком")
    func privacyZones() async {
        let service = FakeZones()
        let model = PrivacyZonesModel(service: service)
        await model.load()
        #expect(model.phase == .loaded && model.zones.isEmpty && model.canAdd)
        #expect(model.radiusMeters == PrivacyZonesModel.previewRadiusMeters)

        for index in 0..<PrivacyZonesModel.limit {
            #expect(await model.add(at: Coordinate(latitude: 52.09 + Double(index) / 100, longitude: 23.7)))
        }
        #expect(model.zones.count == PrivacyZonesModel.limit && !model.canAdd)

        await service.fail(AccountServiceError.rejected(status: 409, code: "zone_limit"))
        #expect(!(await model.add(at: Self.brest)))
        #expect(model.actionError == .rejected(status: 409, code: "zone_limit"))

        await service.fail(nil)
        if let first = model.zones.first {
            await model.remove(first)
        }
        #expect(model.zones.count == PrivacyZonesModel.limit - 1 && model.canAdd && model.actionError == nil)
    }

    @Test("Зоны: нет сервера — «не настроен» без повтора; нет сети — «Повторить» и счёт попыток")
    func privacyZonesStates() async {
        #expect(PrivacyZonesModel(service: nil).phase == .failed(.notConfigured))
        #expect(!RequestFailure.notConfigured.isRetryable)

        let service = FakeZones()
        await service.fail(URLError(.notConnectedToInternet))
        let model = PrivacyZonesModel(service: service)
        await model.load()
        #expect(model.phase == .failed(.offline))
        await service.fail(nil)
        await model.load()
        #expect(model.phase == .loaded && model.attempt == 1)
    }

    // MARK: - «Дом»

    @Test("«Дом»: файл на телефоне, круг 500 м в тумане карты поверх своего, убрать — круга нет")
    func home() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("home-test-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        let store = HomeStore(url: url)
        let home = HomeModel(store: store)
        #expect(home.home == nil && home.circle.isEmpty)

        #expect(home.set(Self.brest))
        #expect(store.load() == Self.brest)
        #expect(!home.circle.isEmpty)

        let map = MapModel(profile: ProfileModel(signedIn: true), home: home)
        let cell = FogGrid.cell(of: Self.brest)
        #expect(map.displayedFog[cell.tile]?.isSet(cell.bitIndex) == true)
        #expect(map.fog.isEmpty)  // свой туман не тронут — круг только в том, что рисует карта

        home.remove()
        #expect(store.load() == nil && home.circle.isEmpty && map.displayedFog.isEmpty)

        try store.save(Self.brest)
        home.reload()
        #expect(home.home == Self.brest)
    }

    // MARK: - Карта: «Где я» и «Бег | Вело»

    @Test("«Где я»: разрешено — центрирование; не спрашивали — подсказка, «Продолжить» — запрос; запрещено — Настройки")
    func locate() async {
        let map = MapModel(profile: ProfileModel(signedIn: true))
        var access = MapLocationAccess.allowed
        var granted = true
        map.locationAccess = { access }
        map.requestLocation = { granted }

        map.locateTapped()
        #expect(map.locateRequest == 1)

        access = .notDetermined
        map.locateTapped()
        #expect(map.locationPrimerShown && map.locateRequest == 1)
        await map.locationPrimerAccepted()
        #expect(map.locateRequest == 2)

        granted = false
        access = .denied
        await map.locationPrimerAccepted()
        #expect(map.locationDeniedShown && map.locateRequest == 2)
    }

    @Test("«Вело» до Сезона 1 — только пояснение, лига остаётся «Бег»")
    func bikeLocked() {
        let map = MapModel(profile: ProfileModel(signedIn: true))
        map.select(.bike)
        #expect(map.bikeHintShown && map.league == .run)
        map.select(.run)
        #expect(!map.bikeHintShown && map.league == .run)
    }

    // MARK: - Профиль и статистика

    @Test("Профиль: согласие из /me, забеги и место из /me/stats, день сезона, статистика из той же сводки")
    func profile() throws {
        let profile = ProfileModel(signedIn: true)
        profile.apply(
            Components.Schemas.MeResponse(
                id: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", displayName: "Бегун-1234", colorIndex: 7, role: "player",
                publicProfile: true))
        profile.apply(
            Components.Schemas.MyStatsResponse(
                runs: 12, distanceMeters: 42_195.5, exploredSquareMeters: 1, season: 0,
                seasonExploredSquareMeters: 1, explorationRank: 7))
        let seasons = Components.Schemas.SeasonsResponse(
            seasons: [.init(number: 0, name: "Сезон 0 (бета)", startsAtMs: 0, endsAtMs: 14 * 86_400_000)], current: 0)
        profile.apply(seasons, nowMs: 9 * 86_400_000 + 1)
        profile.apply(
            Components.Schemas.FogSummaryResponse(layers: [
                .init(layer: .foot, season: nil, tiles: 1, cellCount: 10, areaSquareMeters: 345)
            ]), currentSeason: 0, seasons: seasons)

        #expect(profile.publicProfile == true)
        #expect(profile.runs == 12 && profile.explorationRank == 7)
        #expect(profile.season?.text == "день 10 из 14")
        #expect(profile.exploration?.allTime.cells == 10)
        #expect(profile.seasonExploredSquareMeters == 0)

        #expect(ExplorationStatsModel(api: nil).phase == .failed(.notConfigured))
        #expect(ExplorationStatsModel(api: nil, summary: profile.exploration).phase == .loaded)
        #expect(ExplorationStatsModel(api: nil, failure: .offline).phase == .failed(.offline))
    }
}
