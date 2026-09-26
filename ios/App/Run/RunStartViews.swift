import DesignSystem
import GameCore
import SwiftUI
import UIKit

extension EnvironmentValues {
    /// Экраны забега — модель и пространство переходов (zoom из «Старта» и плашки в HUD). Задаёт оболочка.
    @Entry var runScreens: RunScreenModel? = nil
    @Entry var runTransition: Namespace.ID? = nil
}

/// «Старт» на карте — единственная цветная кнопка (PLAN.md, §6.2): забег не идёт — лист с выбором лиги, идёт — HUD.
/// Одна точка подключения экранов забега к карте: вкладка просто ставит эту кнопку.
struct RunStartButton: View {
    let player: PlayerColor
    @Environment(\.runScreens) private var run
    @Environment(\.runTransition) private var transition

    var body: some View {
        StartButton(player: player) {
            run?.startTapped()
        } label: {
            Label(run?.isRunning == true ? "Забег" : "Старт", systemImage: "figure.run")
        }
        .frame(width: 200)
        .modifier(TransitionSource(id: RunTransitionID.start, namespace: transition))
    }
}

/// Идентификаторы zoom-переходов в HUD.
enum RunTransitionID {
    static let start = "run.start"
    static let accessory = "run.accessory"
}

/// Источник zoom-перехода, если оболочка дала пространство.
struct TransitionSource: ViewModifier {
    let id: String
    let namespace: Namespace.ID?

    func body(content: Content) -> some View {
        if let namespace {
            content.matchedTransitionSource(id: id, in: namespace)
        } else {
            content
        }
    }
}

/// Лист «Старт»: лига («Бег»; «Вело» — с Сезона 1), подсказки к разрешениям (PLAN.md, §6.6: одна кнопка
/// «Продолжить»), «Начать». Контентный слой — без стекла; «Начать» — цветом игрока, как «Старт».
struct RunStartSheet: View {
    @Bindable var model: RunScreenModel
    @Environment(\.openURL) private var openURL

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            switch model.startStep {
            case .league:
                leagueStep
            case .primer(let permission):
                RunPermissionPrimer(permission: permission)
                Spacer(minLength: 0)
                Button("Продолжить") {
                    Task { await model.primerContinue() }
                }
                .buttonStyle(.neutral)
            case .locationDenied:
                RunPermissionPrimer(permission: nil)
                Spacer(minLength: 0)
                Button("Открыть Настройки") {
                    if let url = URL(string: UIApplication.openSettingsURLString) { openURL(url) }
                }
                .buttonStyle(.neutral)
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Palette.uiBackground.color)
        .animation(Motion.numericRoll, value: model.startStep)
    }

    @ViewBuilder
    private var leagueStep: some View {
        Text("Новый забег")
            .font(.title2.bold())
            .foregroundStyle(Palette.uiInk.color)
        VStack(spacing: 10) {
            LeagueOption(
                title: "Бег", detail: "Захват земли и туман «Пешком»", systemImage: "figure.run",
                selected: model.league == .run, locked: false
            ) {
                model.league = .run
            }
            LeagueOption(
                title: "Вело", detail: "Своя лига и своя карта — с Сезона 1", systemImage: "bicycle",
                selected: false, locked: true, action: {})
        }
        if let error = model.startError {
            Label(error, systemImage: "exclamationmark.circle")
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
        }
        Spacer(minLength: 0)
        StartButton(player: model.player) {
            Task { await model.begin() }
        } label: {
            if model.starting {
                ProgressView().tint(model.player.startInkColor)
            } else {
                Label("Начать", systemImage: "figure.run")
            }
        }
        .frame(maxWidth: .infinity)
        .disabled(model.starting)
    }
}

/// Лига на листе «Старт». Закрытая — с замком и пояснением, нажать нельзя.
private struct LeagueOption: View {
    let title: String
    let detail: String
    let systemImage: String
    let selected: Bool
    let locked: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                Image(systemName: systemImage)
                    .font(.title2.weight(.semibold))
                    .frame(width: 44, height: 44)
                    .background(Palette.uiCell2.color, in: .circle)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.headline)
                    Text(detail)
                        .font(.subheadline)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                Spacer(minLength: 8)
                Image(systemName: locked ? "lock.fill" : (selected ? "checkmark.circle.fill" : "circle"))
                    .font(.title3)
                    .foregroundStyle(locked ? Palette.uiInk3.color : Palette.uiInk.color)
            }
            .foregroundStyle(Palette.uiInk.color)
            .padding(14)
            .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
            .overlay {
                RoundedRectangle(cornerRadius: Radius.card)
                    .stroke(selected ? Palette.uiInk.color : .clear, lineWidth: 2)
            }
            .opacity(locked ? 0.55 : 1)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(locked)
        .accessibilityValue(locked ? Text("недоступно до Сезона 1") : (selected ? Text("выбрано") : Text("")))
    }
}

/// Подсказка перед системным запросом (PLAN.md, §5, экран 3; §6.6): зачем разрешение. `nil` — геопозиция запрещена.
/// Тексты — по смыслу строк Info.plist, которые покажет сама система.
struct RunPermissionPrimer: View {
    let permission: RunStartPermission?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Image(systemName: symbolName)
                .font(.system(size: 40, weight: .semibold))
                .foregroundStyle(Palette.uiInk.color)
                .accessibilityHidden(true)
            Text(title)
                .font(.title2.bold())
                .foregroundStyle(Palette.uiInk.color)
            Text(text)
                .font(.body)
                .foregroundStyle(Palette.uiInk2.color)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var symbolName: String {
        switch permission {
        case .location: "location.fill"
        case .motion: "figure.walk.motion"
        case nil: "location.slash.fill"
        }
    }

    private var title: String {
        switch permission {
        case .location: "Геопозиция — на время забега"
        case .motion: "Движение и фитнес"
        case nil: "Геопозиция выключена"
        }
    }

    private var text: String {
        switch permission {
        case .location:
            "Городки записывают след во время забега: так засчитывается захваченная земля и открывается туман. "
                + "Выбери «При использовании» — разрешение «Всегда» игре не нужно."
        case .motion:
            "Датчики движения отличают бег от поездки на транспорте — так игра остаётся честной. Без них забег "
                + "запишется и туман откроется, но захваты сервер не засчитает."
        case nil:
            "Без геопозиции забег не записать. Включи её для Городков в Настройках: «Конфиденциальность → "
                + "Службы геолокации → Городки → При использовании»."
        }
    }
}
