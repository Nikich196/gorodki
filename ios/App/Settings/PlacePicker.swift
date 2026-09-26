import DesignSystem
import GameCore
import MapKit
import SwiftUI

/// Выбор места на карте — «Дом» и приватная зона (PLAN.md, §3.10 и §3.16): карта двигается под неподвижной меткой
/// в центре, круг радиуса `radius` следует за центром. Подложка — как у карты игры (PLAN.md, D4: плоская, `.muted`,
/// без POI). Карта — карточка контента с фиксированной высотой: центр карты и метка совпадают без поправок на
/// безопасную область.
struct PlacePicker: View {
    /// Как нарисовать круг: «Дом» — палитрой тумана (он открывает туман), зона — нейтрально.
    enum Kind {
        case home, privacyZone
    }

    /// Уже поставленные круги — бледнее, чтобы новый не лёг поверх.
    struct Circle: Identifiable, Equatable {
        var id: String
        var center: Coordinate
        var radius: Double
    }

    @Binding var center: Coordinate
    let radius: Double
    let kind: Kind
    var others: [Circle] = []
    @State private var position: MapCameraPosition

    init(center: Binding<Coordinate>, radius: Double, kind: Kind, others: [Circle] = []) {
        _center = center
        self.radius = radius
        self.kind = kind
        self.others = others
        // Круг целиком в кадре с полями: окно ≈ 3,2 радиуса по высоте.
        let span = radius * 3.2 / 111_320
        _position = State(
            initialValue: .region(
                MKCoordinateRegion(
                    center: CLLocationCoordinate2D(
                        latitude: center.wrappedValue.latitude, longitude: center.wrappedValue.longitude),
                    span: MKCoordinateSpan(latitudeDelta: span, longitudeDelta: span * 1.6))))
    }

    var body: some View {
        Map(position: $position, interactionModes: [.pan, .zoom]) {
            ForEach(others) { circle in
                MapCircle(center: circle.center.location, radius: circle.radius)
                    .foregroundStyle(Palette.uiInk.color.opacity(0.06))
                    .stroke(Palette.uiInk3.color, lineWidth: 1)
            }
            MapCircle(center: center.location, radius: radius)
                .foregroundStyle(fill)
                .stroke(edge, lineWidth: 2)
            UserAnnotation()
        }
        .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
        .onMapCameraChange(frequency: .continuous) { context in
            center = Coordinate(
                latitude: context.region.center.latitude, longitude: context.region.center.longitude)
        }
        .overlay {
            // Метка — в центре карты, круг рисует MapKit: метка не двигается, двигается город под ней.
            Image(systemName: kind == .home ? "house.fill" : "eye.slash.fill")
                .font(.system(size: 16, weight: .bold))
                .foregroundStyle(Palette.uiButtonInk.color)
                .frame(width: 34, height: 34)
                .background(Palette.uiButton.color, in: .circle)
                .overlay { SwiftUI.Circle().stroke(Palette.uiCell.color, lineWidth: 2) }
                .shadow(color: .black.opacity(0.18), radius: 4, y: 2)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
        .clipShape(.rect(cornerRadius: Radius.card))
        .accessibilityLabel(
            kind == .home
                ? Text("Карта: двигай, чтобы поставить «Дом»") : Text("Карта: двигай, чтобы выбрать место зоны"))
    }

    private var fill: Color {
        switch kind {
        case .home: FogStyle.exploreFill.color.opacity(0.14)
        case .privacyZone: Palette.uiInk.color.opacity(0.10)
        }
    }

    private var edge: Color {
        switch kind {
        case .home: FogStyle.edge.color
        case .privacyZone: Palette.uiInk2.color
        }
    }
}
