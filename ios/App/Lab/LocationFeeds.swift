import CoreLocation
import Foundation

/// Способ получать геопозицию в фоне. Спайк S1 сравнивает оба на одной прогулке (PLAN.md, §7.2 «Трекинг»).
enum LocationAPI: String, CaseIterable, Identifiable, Codable {
    /// A: `CLLocationUpdate.liveUpdates` + `CLBackgroundActivitySession` (современный способ, iOS 17+).
    case liveUpdates
    /// B: классический `CLLocationManager` с фоновыми обновлениями.
    case locationManager

    var id: String { rawValue }

    var title: String {
        switch self {
        case .liveUpdates: "A · liveUpdates"
        case .locationManager: "B · CLLocationManager"
        }
    }
}

/// Одна отметка GPS — только значения, без объектов CoreLocation: её можно спокойно передавать между потоками.
struct LocationFix: Sendable {
    let latitude: Double
    let longitude: Double
    let horizontalAccuracy: Double
    /// м/с; отрицательная — скорость неизвестна.
    let speed: Double
    let timestamp: Double

    init(_ location: CLLocation) {
        latitude = location.coordinate.latitude
        longitude = location.coordinate.longitude
        horizontalAccuracy = location.horizontalAccuracy
        speed = location.speed
        timestamp = location.timestamp.timeIntervalSince1970
    }
}

/// Источник отметок GPS. Отметки приходят в главном потоке.
@MainActor
protocol LocationFeed: AnyObject {
    func start(onFix: @escaping @MainActor (LocationFix) -> Void)
    func stop()
}

/// Способ A. Сессия фоновой активности держит приложение живым в фоне и показывает синюю плашку в статус-баре;
/// разрешение «При использовании» достаточно — «Всегда» не просим (PLAN.md, §6.6).
@MainActor
final class LiveUpdatesFeed: LocationFeed {
    private var task: Task<Void, Never>?
    private var serviceSession: CLServiceSession?
    private var backgroundSession: CLBackgroundActivitySession?

    func start(onFix: @escaping @MainActor (LocationFix) -> Void) {
        serviceSession = CLServiceSession(authorization: .whenInUse)
        backgroundSession = CLBackgroundActivitySession()
        task = Task {
            do {
                for try await update in CLLocationUpdate.liveUpdates(.fitness) {
                    if let location = update.location {
                        onFix(LocationFix(location))
                    }
                }
            } catch {
                // Поток обновлений закончился (например, при остановке) — ничего делать не нужно.
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
        backgroundSession?.invalidate()
        backgroundSession = nil
        serviceSession?.invalidate()
        serviceSession = nil
    }
}

/// Способ B. Делегат `CLLocationManager` вызывается в потоке, где менеджер создан, — у нас это главный поток.
@MainActor
final class ManagerFeed: NSObject, LocationFeed, CLLocationManagerDelegate {
    private let manager = CLLocationManager()
    private var onFix: (@MainActor (LocationFix) -> Void)?

    func start(onFix: @escaping @MainActor (LocationFix) -> Void) {
        self.onFix = onFix
        manager.delegate = self
        manager.activityType = .fitness
        manager.desiredAccuracy = kCLLocationAccuracyBest
        manager.distanceFilter = kCLDistanceFilterNone
        manager.pausesLocationUpdatesAutomatically = false
        manager.allowsBackgroundLocationUpdates = true
        manager.showsBackgroundLocationIndicator = true
        manager.requestWhenInUseAuthorization()
        manager.startUpdatingLocation()
    }

    func stop() {
        manager.stopUpdatingLocation()
        manager.allowsBackgroundLocationUpdates = false
        onFix = nil
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        let fixes = locations.map(LocationFix.init)
        MainActor.assumeIsolated {
            for fix in fixes {
                onFix?(fix)
            }
        }
    }
}
