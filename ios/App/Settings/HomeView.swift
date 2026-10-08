import DesignSystem
import GameCore
import Observation
import Persistence
import SwiftUI

/// Точка «Дом» (PLAN.md, §3.10): вокруг неё на карте «Исследования» открыт круг 500 м. **Только на телефоне** —
/// сервер её не знает, в статистику она не идёт (`HomeStore`, стирается при выходе и удалении аккаунта). Круг
/// по клеткам тумана считается один раз при смене точки; карта рисует его поверх своего тумана (`HomeCircle.display`).
@MainActor
@Observable
final class HomeModel {
    private(set) var home: Coordinate?
    /// Клетки круга «Дома» по тайлам тумана; без «Дома» — пусто.
    private(set) var circle: [FogTileKey: FogTileBits] = [:]
    /// Растёт при каждой смене — по нему карта перерисовывает туман.
    private(set) var revision = 0
    /// Не удалось записать файл.
    var error: String?
    @ObservationIgnored private let store: HomeStore?

    /// - Parameter store: `nil` — только в памяти (режим фикстур, тесты).
    init(home: Coordinate? = nil, store: HomeStore? = nil) {
        self.store = store
        apply(home ?? store?.load())
    }

    static func live() -> HomeModel {
        HomeModel(store: AppDependencies.shared.home)
    }

    /// Перечитать файл: другой игрок вошёл после выхода прежнего (`wipeLocalData` стёр файл).
    func reload() {
        guard let store else { return }
        let saved = store.load()
        if saved != home {
            apply(saved)
        }
    }

    /// Поставить «Дом» (заменить прежний). `false` — файл не записался, точка прежняя.
    @discardableResult
    func set(_ point: Coordinate) -> Bool {
        do {
            try store?.save(point)
            error = nil
            apply(point)
            return true
        } catch {
            self.error = "Не получилось сохранить «Дом» на телефоне. Попробуй ещё раз."
            return false
        }
    }

    /// Убрать «Дом» — круг с карты пропадёт.
    func remove() {
        do {
            try store?.remove()
            error = nil
            apply(nil)
        } catch {
            self.error = "Не получилось убрать «Дом». Попробуй ещё раз."
        }
    }

    private func apply(_ point: Coordinate?) {
        home = point
        circle = point.map { HomeCircle.fog(around: $0) } ?? [:]
        revision += 1
    }
}

/// «Сменить Дом» (PLAN.md, §5, экран 26): карта с кругом 500 м под неподвижной меткой, «Поставить» и «Убрать».
struct HomeView: View {
    let model: HomeModel
    @State private var center: Coordinate
    @State private var saved = 0
    @State private var removeAsked = false
    @Environment(\.dismiss) private var dismiss

    init(model: HomeModel) {
        self.model = model
        _center = State(initialValue: model.home ?? MapModel.brestCenter)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text(
                    "Отсюда начинается твоё исследование: на слое «Исследование» вокруг «Дома» открыт круг 500 м. "
                        + "Точка хранится только на этом телефоне — сервер её не знает, в статистику она не идёт."
                )
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
                PlacePicker(center: $center, radius: HomeCircle.radiusMeters, kind: .home)
                    .frame(height: 340)
                Label(hint, systemImage: model.home == nil ? "house" : "hand.draw")
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                if let error = model.error {
                    ContentStateView(
                        "Не сохранилось", systemImage: "exclamationmark.triangle", message: error, style: .card)
                }
            }
            .padding(20)
        }
        .background(Palette.uiBackground.color)
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 8) {
                Button(model.home == nil ? "Поставить «Дом» здесь" : "Перенести «Дом» сюда") {
                    if model.set(center) {
                        saved += 1
                        dismiss()
                    }
                }
                .buttonStyle(.neutral)
                if model.home != nil {
                    Button("Убрать «Дом»", role: .destructive) { removeAsked = true }
                        .font(.subheadline.weight(.semibold))
                        .frame(minHeight: 44)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .background(Palette.uiBackground.color)
        }
        .navigationTitle("Дом")
        .navigationBarTitleDisplayMode(.inline)
        .sensoryFeedback(.success, trigger: saved)
        .confirmationDialog("Убрать «Дом»?", isPresented: $removeAsked, titleVisibility: .visible) {
            Button("Убрать", role: .destructive) { model.remove() }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text("Круг вокруг «Дома» на карте закроется туманом. Твой открытый туман не изменится.")
        }
    }

    private var hint: String {
        model.home == nil ? "«Дом» не поставлен — двигай карту и поставь" : "Двигай карту, чтобы перенести «Дом»"
    }
}
