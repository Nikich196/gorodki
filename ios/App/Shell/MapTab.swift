import DesignSystem
import MapKit
import SwiftUI

/// Вкладка «Карта» до этапа 2: подложка Apple Maps как у игры (PLAN.md, D4: плоская, приглушённая, без POI) над
/// Брестом и плавающий «Старт» — единственный цветной элемент управления (docs/design/tokens.md, §6). «Старт» ведёт
/// в экраны забега (`RunStartButton`); земли, туман и режимы появятся здесь со следующими шагами.
struct MapTab: View {
    let player: PlayerColor

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
                RunStartButton(player: player)
                    .padding(.bottom, 12)
            }
    }
}
