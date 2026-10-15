import Platform
import WidgetKit

/// Снимок профиля для виджета «Моя земля» (`TerritorySnapshot` в App Group): пишется, когда профиль загрузился с
/// сервера, стирается при любой смене входа — виджет не показывает землю прежнего игрока.
@MainActor
enum WidgetPublisher {
    static func publish(_ profile: ProfileModel) {
        guard profile.signedIn, let name = profile.displayName else { return }
        let snapshot = TerritorySnapshot(
            displayName: name, colorIndex: profile.colorIndex ?? 0, seasonName: profile.season?.name,
            seasonDay: profile.season?.day, seasonDays: profile.season?.days,
            seasonSquareMeters: profile.seasonExploredSquareMeters, allTimeSquareMeters: profile.exploredSquareMeters,
            explorationRank: profile.explorationRank, updatedAt: .now)
        guard snapshot != TerritorySnapshot.load().map({ with($0, updatedAt: snapshot.updatedAt) }) else { return }
        if snapshot.save() {
            WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.territory)
        }
    }

    static func clear() {
        TerritorySnapshot.remove()
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.territory)
    }

    /// Тот же снимок с другим временем — чтобы не перерисовывать виджет, когда числа не изменились.
    private static func with(_ snapshot: TerritorySnapshot, updatedAt: Date) -> TerritorySnapshot {
        var copy = snapshot
        copy.updatedAt = updatedAt
        return copy
    }
}
