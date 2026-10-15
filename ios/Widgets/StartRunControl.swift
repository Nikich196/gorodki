import AppIntents
import Platform
import SwiftUI
import WidgetKit

/// «Старт забега» в Пункте управления, на экране блокировки и на кнопке действия (дизайн-проход 15.10): открывает
/// «Городки» сразу на листе «Новый забег» (`gorodki://start`). Забег без подтверждения не начинается — лига и
/// разрешения, как с «Старта» на карте.
struct StartRunControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: WidgetKind.startRun) {
            ControlWidgetButton(action: OpenURLIntent(AppLink.start)) {
                Label("Старт забега", systemImage: "figure.run")
            }
            .tint(.orange)
        }
        .displayName("Старт забега")
        .description("Открывает «Городки» на листе «Новый забег».")
    }
}
