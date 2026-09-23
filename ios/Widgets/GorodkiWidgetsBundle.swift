import SwiftUI
import WidgetKit

/// Точка входа расширения: все виджеты и Live Activity «Городков».
@main
struct GorodkiWidgetsBundle: WidgetBundle {
    var body: some Widget {
        InstallCheckWidget()
        RunLiveActivityWidget()
    }
}
