import DesignSystem
import MapKit
import SwiftUI

/// Вкладка «Карта» до этапа 2: подложка Apple Maps как у игры (PLAN.md, D4: плоская, приглушённая, без POI) над
/// Брестом и плавающий «Старт» — единственный цветной элемент управления (docs/design/tokens.md, §6). Земли, туман,
/// режимы и HUD появятся здесь со следующими шагами.
struct MapTab: View {
    let player: PlayerColor
    @State private var startNoticeShown = false

    /// Центр Бреста.
    private static let brest = MKCoordinateRegion(
        center: CLLocationCoordinate2D(latitude: 52.0976, longitude: 23.7341),
        span: MKCoordinateSpan(latitudeDelta: 0.06, longitudeDelta: 0.06))

    var body: some View {
        Map(initialPosition: .region(Self.brest))
            .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
            .overlay(alignment: .top) {
                Label("Земли и туман — скоро", systemImage: "square.dashed")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .padding(.horizontal, 16)
                    .frame(height: Metrics.modeRowHeight)
                    .capsuleGlass()
                    .padding(.top, 8)
            }
            .safeAreaInset(edge: .bottom) {
                StartButton(player: player) {
                    startNoticeShown = true
                } label: {
                    Label("Старт", systemImage: "figure.run")
                }
                .frame(width: 200)
                .padding(.bottom, 12)
            }
            .alert("Забег — скоро", isPresented: $startNoticeShown) {
                Button("Понятно", role: .cancel) {}
            } message: {
                Text(startNotice)
            }
    }

    private var startNotice: String {
        let hud = "Экран забега появится вместе с картой земель."
        return DebugAccess.buildAllows ? hud + " Пробный забег без сервера — в «Профиль → Отладка → Лаборатория»." : hud
    }
}
