import DesignSystem
import GameCore
import SwiftUI
import Sync

/// Полноэкранный слой забега: HUD, а после «Финиша» — итог на его месте (PLAN.md, §5: «Забег — модальный HUD»).
struct RunCover: View {
    @Bindable var model: RunScreenModel

    var body: some View {
        Group {
            if let result = model.result, !model.isRunning {
                NavigationStack {
                    RunResultView(model: result) {
                        model.coverShown = false
                    }
                }
                .transition(.opacity.combined(with: .scale(scale: 0.96)))
            } else {
                RunHUDView(model: model)
                    .transition(.opacity)
            }
        }
        .animation(Motion.captureCardDismiss, value: model.result?.runId)
    }
}

/// HUD забега (PLAN.md, §6.7; макет — design/APPROVALS.md): карта со следом, сверху — плашки, снизу — одна стеклянная
/// панель: «До замыкания 140 м» со стрелкой и кольцом, «Замкни петлю — будет ≈ +1,2 га», три метрики, «+N га тумана»,
/// кнопки 64 pt: «Свернуть», «Финиш — удерживай», настройки голоса и вибрации. Поверх — церемония захвата.
struct RunHUDView: View {
    @Bindable var model: RunScreenModel
    @State private var compass = HeadingSource()

    var body: some View {
        ZStack {
            RunMapView(
                trail: model.trail, contours: model.contours, target: model.readout.closure.readout?.target,
                player: model.player
            )
            .ignoresSafeArea()
            VStack(spacing: 8) {
                RunTopPlaques(model: model)
                Spacer(minLength: 0)
                HUDPanel(model: model)
            }
            .padding(.horizontal, Metrics.panelInset)
            .padding(.bottom, 4)
            if let playing = model.stage.playing {
                CaptureCeremonyView(
                    item: playing, ring: model.ceremonyRing, player: model.player, decisions: model.decisions
                ) {
                    model.continueRun()
                }
                .id(playing.id)
                .transition(.opacity)
                .zIndex(1)
            }
        }
        .animation(Motion.captureCardDismiss, value: model.stage.playing?.id)
        .sheet(isPresented: $model.settingsShown) {
            RunSettingsSheet(model: model)
                .presentationDetents([.height(300)])
        }
        .sheet(isPresented: $model.missedShown, onDismiss: { model.dismissMissed() }) {
            MissedCeremoniesSheet(items: model.stage.missed, player: model.player) {
                model.dismissMissed()
            }
            .presentationDetents([.medium, .large])
        }
        .confirmationDialog("Закончить забег?", isPresented: $model.finishAsked, titleVisibility: .visible) {
            Button("Финиш", role: .destructive) {
                Task { await model.finish() }
            }
            Button("Продолжить забег", role: .cancel) {}
        } message: {
            Text("Забег сохранится и уйдёт на сервер. Удерживай «Финиш», чтобы закончить без вопроса.")
        }
        .onAppear { compass.start() }
        .onDisappear { compass.stop() }
        .onChange(of: compass.heading) { _, heading in model.heading = heading }
        .task {
            // Раз в секунду: таймер повтора и «гаснущие» плашки.
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1))
                model.tick()
            }
        }
    }
}

/// Сверху — только плашки-предупреждения (макет: «сверху только предупреждения и шкала „горячо“»), тосты второй фазы
/// и «ПОВТОР».
private struct RunTopPlaques: View {
    let model: RunScreenModel

    var body: some View {
        VStack(spacing: 8) {
            if model.readout.isReplay {
                Text("ПОВТОР")
                    .font(.caption.weight(.heavy))
                    .tracking(1.2)
                    .foregroundStyle(Palette.uiInk.color)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .capsuleGlass()
            }
            ForEach(Array(model.readout.warnings.prefix(2).enumerated()), id: \.offset) { _, warning in
                WarningPlaque(warning: warning)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
            ForEach(model.stage.toasts) { item in
                DecisionToast(item: item) { model.dismissToast(item.id) }
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .animation(Motion.numericRoll, value: model.readout.warnings.map(\.kind))
        .animation(Motion.numericRoll, value: model.stage.toasts.map(\.id))
    }
}

/// Плашка-предупреждение на стекле: значок, заголовок и что делать.
struct WarningPlaque: View {
    let warning: RunWarning

    var body: some View {
        let text = warning.text
        HStack(spacing: 12) {
            Image(systemName: Self.symbolName(warning))
                .font(.subheadline.weight(.bold))
                .foregroundStyle(text.isSevere ? Palette.warnInk.color : Palette.uiInk.color)
                .frame(width: 32, height: 32)
                .background(
                    text.isSevere ? Palette.warn.color : Palette.uiFill.color, in: .rect(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 1) {
                Text(text.title)
                    .font(.subheadline.weight(.semibold))
                Text(text.detail)
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
                    .lineLimit(2)
            }
            .foregroundStyle(Palette.uiInk.color)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .glassEffect(.regular, in: .rect(cornerRadius: Radius.plaque))
        .accessibilityElement(children: .combine)
    }

    static func symbolName(_ warning: RunWarning) -> String {
        switch warning.kind {
        case .storageFailed: "externaldrive.badge.exclamationmark"
        case .queueNotDurable: "tray.and.arrow.down"
        case .motionMissing: "figure.walk.motion"
        case .signInNeeded: "person.crop.circle.badge.exclamationmark"
        case .clockInvalid: "clock.badge.exclamationmark"
        case .accountDeleting: "person.crop.circle.badge.xmark"
        case .trackBroken: warning.issue == .vehicle || warning.issue == .tooFast ? "car.fill" : "scissors"
        case .weakGps: "location.slash"
        case .resumed: "arrow.clockwise"
        case .serverUnavailable: "icloud.slash"
        }
    }
}

/// Вторая фаза для уже закрытой церемонии — короткой строкой, сама уходит через 4 с.
private struct DecisionToast: View {
    let item: CeremonyItem
    let dismiss: () -> Void
    @State private var shown = 0

    var body: some View {
        let applied = item.decision?.applied == true
        HStack(spacing: 10) {
            Image(systemName: applied ? "checkmark.seal.fill" : "xmark.octagon.fill")
                .font(.headline)
                .symbolEffect(.bounce, value: shown)
            Text(item.title + " · " + item.status)
                .font(.subheadline.weight(.semibold))
                .lineLimit(2)
        }
        .foregroundStyle(Palette.uiInk.color)
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .capsuleGlass()
        .onTapGesture(perform: dismiss)
        .task {
            shown += 1
            try? await Task.sleep(for: .seconds(4))
            dismiss()
        }
    }
}

// MARK: - Панель

/// Одна стеклянная панель снизу (макет HUD): «до замыкания», метрики, туман, кнопки.
private struct HUDPanel: View {
    @Bindable var model: RunScreenModel

    var body: some View {
        let readout = model.readout
        VStack(alignment: .leading, spacing: 12) {
            ClosureBlock(model: model)
            Divider()
            HStack(alignment: .firstTextBaseline, spacing: 0) {
                Metric(value: readout.distanceText, unit: "км", title: "Дистанция")
                Divider().frame(height: 40)
                Metric(value: readout.paceText, unit: NumberText.paceUnit, title: "Темп")
                Divider().frame(height: 40)
                TimerMetric(model: model)
            }
            FogChip(text: readout.fogText)
            if let error = model.finishError {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            GlassEffectContainer(spacing: 10) {
                HStack(spacing: 10) {
                    RoundGlassButton(title: "Свернуть", systemImage: "chevron.down") {
                        model.collapse()
                    }
                    HoldToFinishButton {
                        Task { await model.finish() }
                    } ask: {
                        model.finishAsked = true
                    }
                    RoundGlassButton(title: "Голос и вибрация", systemImage: "speaker.wave.2.fill") {
                        model.settingsShown = true
                    }
                }
            }
        }
        .padding(18)
        .panelGlass()
    }
}

/// «До замыкания 140 м» + стрелка к точке замыкания с кольцом (сколько пройдено к цели) и строка «что делать».
private struct ClosureBlock: View {
    let model: RunScreenModel

    var body: some View {
        let closure = model.readout.closure
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .center, spacing: 12) {
                VStack(alignment: .leading, spacing: 0) {
                    Text(closure.caption)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk2.color)
                    if let meters = closure.meters {
                        HStack(alignment: .firstTextBaseline, spacing: 4) {
                            Text(NumberText.integer(meters))
                                .scaledFont(.hudHero)
                                .contentTransition(.numericText(value: Double(meters)))
                                .animation(Motion.numericRoll, value: meters)
                            Text("м")
                                .scaledFont(.hudHeroUnit)
                        }
                        .lineLimit(1)
                        .minimumScaleFactor(0.6)
                        .accessibilityElement(children: .combine)
                    } else if let headline = closure.headline {
                        Text(headline)
                            .font(.system(size: 44, weight: .bold, design: .rounded))
                            .lineLimit(1)
                            .minimumScaleFactor(0.6)
                    }
                }
                .foregroundStyle(Palette.uiInk.color)
                Spacer(minLength: 0)
                if closure.meters != nil {
                    ArrowDial(
                        angle: model.arrowAngle, progress: model.ringProgress, pulses: model.cuePulses,
                        player: model.player)
                }
            }
            Text(closure.hint())
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
                .fixedSize(horizontal: false, vertical: true)
                .contentTransition(.opacity)
        }
    }
}

/// Стрелка к точке замыкания в круге (день — тёмный, ночь — светлый: нейтральная кнопка темы) и кольцо вокруг:
/// `trim` — сколько пройдено к цели; на сигналах 50/15 м кольцо пульсирует.
private struct ArrowDial: View {
    let angle: Double?
    let progress: Double?
    let pulses: Int
    let player: PlayerColor
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack {
            Circle()
                .stroke(Palette.uiFill.color, lineWidth: 5)
            Circle()
                .trim(from: 0, to: progress ?? 0)
                .stroke(player.edgeColor, style: StrokeStyle(lineWidth: 5, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.smooth(duration: 0.6).unlessReduceMotion(reduceMotion), value: progress)
            Circle()
                .fill(Palette.uiButton.color)
                .padding(8)
            if let angle {
                Image(systemName: "location.north.fill")
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(Palette.uiButtonInk.color)
                    .rotationEffect(.degrees(angle))
                    .animation(.smooth(duration: 0.4).unlessReduceMotion(reduceMotion), value: angle)
            } else {
                Circle()
                    .fill(Palette.uiButtonInk.color)
                    .frame(width: 8, height: 8)
            }
        }
        .frame(width: 76, height: 76)
        .keyframeAnimator(initialValue: 1.0, trigger: pulses) { content, scale in
            content.scaleEffect(reduceMotion ? 1 : scale)
        } keyframes: { _ in
            KeyframeTrack {
                SpringKeyframe(1.16, duration: 0.18, spring: .snappy)
                SpringKeyframe(1.0, duration: 0.5, spring: .bouncy)
            }
        }
        .accessibilityElement()
        .accessibilityLabel(Text(angle == nil ? "Направление неизвестно" : "Стрелка к точке замыкания"))
    }
}

/// Метрика HUD: «3,21 км / Дистанция».
private struct Metric: View {
    let value: String
    let unit: String
    let title: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline, spacing: 2) {
                Text(value)
                    .scaledFont(.hudMetric)
                    .contentTransition(.numericText())
                    .animation(Motion.numericRoll, value: value)
                Text(unit)
                    .scaledFont(.hudMetricUnit)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            Text(title)
                .font(.caption)
                .foregroundStyle(Palette.uiInk2.color)
        }
        .foregroundStyle(Palette.uiInk.color)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 6)
        .accessibilityElement(children: .combine)
    }
}

/// Время забега: у живого — `Text(timerInterval:)` (PLAN.md, §6.7, цифры идут сами), у повтора — по времени точек.
private struct TimerMetric: View {
    let model: RunScreenModel

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Group {
                if let startedAtMs = model.state.startedAtMs, !model.readout.isReplay {
                    let start = Date(timeIntervalSince1970: Double(startedAtMs) / 1_000)
                    Text(timerInterval: start...Date.distantFuture, countsDown: false)
                } else {
                    Text(model.readout.elapsedText)
                        .contentTransition(.numericText())
                }
            }
            .scaledFont(.hudMetric)
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            Text("Время")
                .font(.caption)
                .foregroundStyle(Palette.uiInk2.color)
        }
        .foregroundStyle(Palette.uiInk.color)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 6)
        .accessibilityElement(children: .combine)
    }
}

/// «+0,8 га тумана» — значок цветом «открыто» тумана (tokens.md, §2).
struct FogChip: View {
    let text: String

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "cloud.fog.fill")
                .foregroundStyle(FogStyle.exploreFill.color)
            Text(text)
                .font(.subheadline.weight(.semibold).monospacedDigit())
                .contentTransition(.numericText())
                .animation(Motion.numericRoll, value: text)
        }
        .foregroundStyle(Palette.uiInk.color)
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .background(Palette.uiFill.color, in: .capsule)
    }
}

/// Круглая кнопка HUD 64 pt на стекле.
private struct RoundGlassButton: View {
    let title: String
    let systemImage: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.title3.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
                .frame(width: Metrics.controlHeight, height: Metrics.controlHeight)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .circle)
        .accessibilityLabel(Text(title))
    }
}

/// «Финиш — удерживай» (макет HUD): удержание 1 с заполняет капсулу и заканчивает забег; простое нажатие спрашивает
/// подтверждение — так забег не закончится от случайного касания в кармане.
private struct HoldToFinishButton: View {
    let finish: () -> Void
    let ask: () -> Void
    @State private var holding = false
    @State private var finished = false

    private static let holdSeconds = 1.0

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "stop.fill")
                .font(.subheadline.weight(.bold))
            VStack(alignment: .leading, spacing: 0) {
                Text("Финиш")
                    .font(.headline)
                Text("удерживай")
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        }
        .foregroundStyle(Palette.uiInk.color)
        .frame(maxWidth: .infinity, minHeight: Metrics.controlHeight)
        .background(alignment: .leading) {
            GeometryReader { proxy in
                Capsule()
                    .fill(Palette.uiInk.color.opacity(0.14))
                    .frame(width: holding ? proxy.size.width : 0)
                    .animation(
                        holding ? .linear(duration: Self.holdSeconds) : .easeOut(duration: 0.2), value: holding)
            }
        }
        .clipShape(.capsule)
        .contentShape(.capsule)
        .glassEffect(.regular.interactive(), in: .capsule)
        .onTapGesture {
            if !finished { ask() }
        }
        .onLongPressGesture(minimumDuration: Self.holdSeconds) {
            finished = true
            finish()
        } onPressingChanged: { pressing in
            holding = pressing
        }
        .sensoryFeedback(.impact(weight: .heavy), trigger: finished) { _, new in new }
        .accessibilityElement()
        .accessibilityLabel(Text("Финиш"))
        .accessibilityHint(Text("Спросит подтверждение"))
        .accessibilityAddTraits(.isButton)
        .accessibilityAction { ask() }
    }
}

// MARK: - Листы

/// Настройки забега: голос и вибрация на сигналах «до замыкания», петлях и разрывах следа (PLAN.md, §6.9).
private struct RunSettingsSheet: View {
    @Bindable var model: RunScreenModel

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("Голос в кармане", isOn: $model.settings.voice)
                    Toggle("Вибрация на экране", isOn: $model.settings.haptics)
                } footer: {
                    Text(
                        "Сигналы «до замыкания» 50 и 15 м, замкнутая петля, решение сервера и разрыв следа. На экране — "
                            + "вибрация, в кармане — голос поверх музыки.")
                }
            }
            .navigationTitle("Голос и вибрация")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Готово") { model.settingsShown = false }
                }
            }
        }
    }
}

/// Пропущенные церемонии — списком, когда экран снова включили (решение 25.09, пункт 8).
private struct MissedCeremoniesSheet: View {
    let items: [CeremonyItem]
    let player: PlayerColor
    let done: () -> Void

    var body: some View {
        NavigationStack {
            List(items) { item in
                HStack(spacing: 14) {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(player.fillColor(.three))
                        .overlay { RoundedRectangle(cornerRadius: 6).stroke(player.edgeColor, lineWidth: 2) }
                        .frame(width: 30, height: 30)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.title + " · " + CeremonyText.estimate(item.estimatedSquareMeters))
                            .font(.headline)
                        Text(item.status)
                            .font(.subheadline)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
                .listRowBackground(Palette.uiCell.color)
            }
            .scrollContentBackground(.hidden)
            .background(Palette.uiBackground.color)
            .navigationTitle("Пока экран был выключен")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Понятно", action: done)
                }
            }
        }
    }
}

// MARK: - Плашка свёрнутого забега

/// Свёрнутый забег — плашка `tabViewBottomAccessory` над таб-баром (PLAN.md, §5): время, дистанция и «до замыкания»;
/// нажатие открывает HUD.
struct RunAccessory: View {
    let model: RunScreenModel
    @Environment(\.tabViewBottomAccessoryPlacement) private var placement
    @Environment(\.runTransition) private var transition

    var body: some View {
        let readout = model.readout
        Button {
            model.expand()
        } label: {
            HStack(spacing: 10) {
                Image(systemName: "figure.run")
                    .font(.headline)
                    .foregroundStyle(model.player.edgeColor)
                if let startedAtMs = model.state.startedAtMs, !readout.isReplay {
                    Text(
                        timerInterval: Date(timeIntervalSince1970: Double(startedAtMs) / 1_000)...Date.distantFuture,
                        countsDown: false
                    )
                    .font(.headline.monospacedDigit())
                    .frame(maxWidth: 70, alignment: .leading)
                } else {
                    Text(readout.elapsedText)
                        .font(.headline.monospacedDigit())
                }
                Text(readout.distanceText + NumberText.unitSeparator + "км")
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(Palette.uiInk2.color)
                Spacer(minLength: 4)
                if placement != .inline, let meters = readout.closure.meters {
                    Text(readout.closure.caption + " " + NumberText.meters(meters))
                        .font(.subheadline.weight(.semibold).monospacedDigit())
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                }
                if !model.stage.missed.isEmpty {
                    Image(systemName: "seal.fill")
                        .foregroundStyle(model.player.edgeColor)
                        .accessibilityLabel(Text("Есть замкнутые петли"))
                }
                Image(systemName: "chevron.up")
                    .font(.footnote.weight(.bold))
                    .foregroundStyle(Palette.uiInk3.color)
            }
            .foregroundStyle(Palette.uiInk.color)
            .padding(.horizontal, 16)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .modifier(TransitionSource(id: RunTransitionID.accessory, namespace: transition))
        .accessibilityLabel(Text("Забег идёт. Открыть"))
    }
}
