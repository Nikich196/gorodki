import DesignSystem
import GameCore
import MapKit
import Networking
import Observation
import SwiftUI

/// Приватная зона на экране: центр, радиус (его назначает сервер) и когда поставлена.
struct PrivacyZone: Identifiable, Equatable, Sendable {
    var id: String
    var center: Coordinate
    var radiusMeters: Double
    var createdAtMs: Int64
}

/// Зоны приватности на сервере (`/me/privacy-zones`) — через протокол, чтобы модель проверялась без сети.
protocol PrivacyZoneService: Sendable {
    func zones() async throws -> [PrivacyZone]
    func add(at center: Coordinate) async throws -> PrivacyZone
    func remove(id: String) async throws
}

extension AccountService: PrivacyZoneService {
    func zones() async throws -> [PrivacyZone] {
        try await privacyZones().map { PrivacyZone($0) }
    }

    func add(at center: Coordinate) async throws -> PrivacyZone {
        PrivacyZone(try await addPrivacyZone(latitude: center.latitude, longitude: center.longitude))
    }

    func remove(id: String) async throws {
        try await removePrivacyZone(id: id)
    }
}

extension PrivacyZone {
    init(_ zone: AccountService.PrivacyZone) {
        self.init(
            id: zone.id, center: Coordinate(latitude: zone.lat, longitude: zone.lon), radiusMeters: zone.radiusMeters,
            createdAtMs: zone.createdAtMs)
    }
}

/// «Приватные зоны» (PLAN.md, §3.16; §5, экран 24): список, добавление на карте, удаление. Внутри зоны визиты
/// не засчитываются (docs/legal/privacy-v1.md, `VisitProcessor` сервера). Радиус и предел числа зон задаёт сервер
/// (`privacy.zoneRadiusMeters`, `privacy.maxZones`); здесь — те же числа игрового конфига v1 для подсказки и круга
/// на карте до ответа сервера.
@MainActor
@Observable
final class PrivacyZonesModel {
    /// Радиус зоны в конфиге v1 (contracts/game-config.v1.json, `privacy.zoneRadiusMeters`) — круг на карте до ответа.
    static let previewRadiusMeters = 400.0
    /// Сколько зон можно (`privacy.maxZones`); сервер проверяет сам (409 `zone_limit`).
    static let limit = 3

    private(set) var phase = LoadPhase.loading
    private(set) var zones: [PrivacyZone] = []
    /// Идёт добавление или удаление.
    private(set) var busy = false
    /// Ошибка добавления или удаления — под списком, список остаётся.
    var actionError: RequestFailure?
    /// Сколько раз повторяли загрузку — значок состояния подпрыгивает.
    private(set) var attempt = 0
    /// Растёт при удачном добавлении или удалении — вибрация.
    private(set) var changes = 0
    @ObservationIgnored private let service: (any PrivacyZoneService)?

    /// - Parameter service: `nil` — адрес сервера не задан.
    init(service: (any PrivacyZoneService)?, zones: [PrivacyZone]? = nil) {
        self.service = service
        if let zones {
            self.zones = zones
            phase = .loaded
        } else if service == nil {
            phase = .failed(.notConfigured)
        }
    }

    static func live() -> PrivacyZonesModel {
        PrivacyZonesModel(service: AppDependencies.shared.account)
    }

    var canAdd: Bool { phase == .loaded && zones.count < Self.limit && !busy }

    /// Радиус новой зоны на карте: как у уже поставленных (его назначил сервер), иначе из конфига.
    var radiusMeters: Double { zones.first?.radiusMeters ?? Self.previewRadiusMeters }

    func load() async {
        guard let service else {
            phase = .failed(.notConfigured)
            return
        }
        if case .failed = phase {
            attempt += 1
            phase = .loading
        }
        do {
            zones = try await service.zones().sorted { $0.createdAtMs < $1.createdAtMs }
            phase = .loaded
        } catch {
            guard !Task.isCancelled else { return }
            phase = .failed(RequestFailure(error))
        }
    }

    /// Новая зона с центром в точке. `true` — сервер принял.
    func add(at center: Coordinate) async -> Bool {
        guard let service, !busy else { return false }
        busy = true
        defer { busy = false }
        do {
            let zone = try await service.add(at: center)
            zones.append(zone)
            actionError = nil
            changes += 1
            return true
        } catch {
            actionError = RequestFailure(error)
            return false
        }
    }

    func remove(_ zone: PrivacyZone) async {
        guard let service, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await service.remove(id: zone.id)
            zones.removeAll { $0.id == zone.id }
            actionError = nil
            changes += 1
        } catch {
            actionError = RequestFailure(error)
        }
    }
}

/// Экран «Приватные зоны»: карта со всеми зонами, список, «Добавить зону».
struct PrivacyZonesView: View {
    @State var model: PrivacyZonesModel
    @State private var removing: PrivacyZone?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        content
            .background(Palette.uiBackground.color)
            .navigationTitle("Приватные зоны")
            .navigationBarTitleDisplayMode(.inline)
            .task { if model.phase == .loading { await model.load() } }
            .sensoryFeedback(.success, trigger: model.changes)
            .confirmationDialog(
                "Убрать зону?", isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }),
                titleVisibility: .visible, presenting: removing
            ) { zone in
                Button("Убрать", role: .destructive) {
                    Task { await model.remove(zone) }
                }
                Button("Отмена", role: .cancel) {}
            } message: { _ in
                Text("Визиты у этого места снова будут засчитываться.")
            }
    }

    @ViewBuilder
    private var content: some View {
        switch model.phase {
        case .loading:
            ContentLoadingView("Загружаю зоны…")
        case .failed(let failure):
            ContentStateView(failure, attempt: model.attempt) {
                Task { await model.load() }
            }
        case .loaded:
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    Text(
                        "Внутри зоны визиты на участки не засчитываются — поставь её, например, у дома или общежития. "
                            + "Радиус зоны — \(NumberText.meters(Int(model.radiusMeters)))."
                    )
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    if model.zones.isEmpty {
                        ContentStateView(
                            "Зон пока нет", systemImage: "eye.slash",
                            message: "Добавь место, где часто начинаешь забеги, — например, дом.", style: .card)
                    } else {
                        ZonesOverview(zones: model.zones)
                            .frame(height: 220)
                        zoneList
                    }
                    if let failure = model.actionError {
                        ContentStateView(failure, style: .card)
                    }
                }
                .padding(20)
                .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.zones)
            }
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 6) {
                    NavigationLink {
                        AddPrivacyZoneView(model: model)
                    } label: {
                        Text("Добавить зону")
                    }
                    .buttonStyle(.neutral)
                    .disabled(!model.canAdd)
                    if model.zones.count >= PrivacyZonesModel.limit {
                        Text("Зон может быть не больше \(PrivacyZonesModel.limit) — убери одну, чтобы добавить новую.")
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                            .multilineTextAlignment(.center)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                .background(Palette.uiBackground.color)
            }
        }
    }

    private var zoneList: some View {
        VStack(spacing: 0) {
            ForEach(Array(model.zones.enumerated()), id: \.element.id) { index, zone in
                HStack(spacing: 14) {
                    Image(systemName: "eye.slash.fill")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Palette.uiInk2.color)
                        .frame(width: 36, height: 36)
                        .background(Palette.uiCell2.color, in: .circle)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Зона \(index + 1)")
                            .font(.body.weight(.semibold))
                            .foregroundStyle(Palette.uiInk.color)
                        Text(PrivacyZoneText.subtitle(zone))
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    Spacer(minLength: 8)
                    Button {
                        removing = zone
                    } label: {
                        Label("Убрать зону \(index + 1)", systemImage: "trash")
                            .labelStyle(.iconOnly)
                            .font(.body.weight(.semibold))
                            .foregroundStyle(Palette.uiInk2.color)
                            .frame(width: 44, height: 44)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .disabled(model.busy)
                }
                .padding(.horizontal, 14)
                .frame(minHeight: 60)
                .transition(.opacity)
                if zone != model.zones.last {
                    Divider().padding(.leading, 64)
                }
            }
        }
        .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
    }
}

/// Строки зоны: «радиус 400 м · с 26 сентября».
enum PrivacyZoneText {
    static func subtitle(_ zone: PrivacyZone, timeZone: TimeZone = .current) -> String {
        let date = Date(timeIntervalSince1970: Double(zone.createdAtMs) / 1_000)
        var format = Date.FormatStyle.dateTime.day().month(.wide).locale(NumberText.locale)
        format.timeZone = timeZone
        return "радиус \(NumberText.meters(Int(zone.radiusMeters.rounded()))) · с \(date.formatted(format))"
    }
}

/// Все зоны на одной карте — чтобы видеть, что закрыто.
private struct ZonesOverview: View {
    let zones: [PrivacyZone]

    var body: some View {
        Map(initialPosition: .rect(Self.rect(zones)), interactionModes: [.pan, .zoom]) {
            ForEach(zones) { zone in
                MapCircle(center: zone.center.location, radius: zone.radiusMeters)
                    .foregroundStyle(Palette.uiInk.color.opacity(0.10))
                    .stroke(Palette.uiInk2.color, lineWidth: 2)
            }
        }
        .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
        .clipShape(.rect(cornerRadius: Radius.card))
        .accessibilityLabel(Text("Карта приватных зон"))
    }

    /// Все круги в кадре с полями.
    private static func rect(_ zones: [PrivacyZone]) -> MKMapRect {
        var result = MKMapRect.null
        for zone in zones {
            let center = MKMapPoint(zone.center.location)
            let points = zone.radiusMeters * MKMapPointsPerMeterAtLatitude(zone.center.latitude)
            result = result.union(
                MKMapRect(x: center.x - points, y: center.y - points, width: 2 * points, height: 2 * points))
        }
        let margin = max(result.width, result.height) * 0.25
        return result.insetBy(dx: -margin, dy: -margin)
    }
}

/// Добавление зоны: карта под неподвижной меткой, круг радиуса зоны, «Сделать приватной».
private struct AddPrivacyZoneView: View {
    let model: PrivacyZonesModel
    @State private var center: Coordinate
    @Environment(\.dismiss) private var dismiss

    init(model: PrivacyZonesModel) {
        self.model = model
        _center = State(initialValue: model.zones.last?.center ?? MapModel.brestCenter)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Двигай карту, пока метка не встанет на нужное место.")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                PlacePicker(
                    center: $center, radius: model.radiusMeters, kind: .privacyZone,
                    others: model.zones.map { .init(id: $0.id, center: $0.center, radius: $0.radiusMeters) }
                )
                .frame(height: 380)
                if let failure = model.actionError {
                    ContentStateView(failure, style: .card)
                }
            }
            .padding(20)
        }
        .background(Palette.uiBackground.color)
        .safeAreaInset(edge: .bottom) {
            Button {
                Task {
                    if await model.add(at: center) {
                        dismiss()
                    }
                }
            } label: {
                if model.busy {
                    ProgressView().tint(Palette.uiButtonInk.color)
                } else {
                    Text("Сделать приватной")
                }
            }
            .buttonStyle(.neutral)
            .disabled(model.busy)
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .background(Palette.uiBackground.color)
        }
        .navigationTitle("Новая зона")
        .navigationBarTitleDisplayMode(.inline)
    }
}
