import DesignSystem
import Networking
import SwiftUI

/// Онбординг до входа (PLAN.md, §3.18): интро → код приглашения → 16+ → соглашение → согласие → Google. Контентный
/// слой — без стекла; главная кнопка — нейтральная (`.neutral`), цвет игрока появится только на «Старте».
struct OnboardingView: View {
    @Bindable var model: OnboardingModel
    /// Посмотреть вкладки без входа — только в сборках команды (`DebugAccess.buildAllows`).
    var browseWithoutSignIn: (@MainActor @Sendable () -> Void)?
    /// «Посмотреть демо» на интро; `nil` — кнопки нет (режим фикстур для снимков онбординга).
    var showDemo: (@MainActor @Sendable () -> Void)? = nil
    @State private var debugMenuShown = false

    var body: some View {
        NavigationStack(path: $model.path) {
            IntroStep(model: model, showDemo: showDemo)
                .navigationDestination(for: OnboardingStep.self) { step in
                    stepView(step)
                        .toolbar { debugToolbar }
                }
                .toolbar { debugToolbar }
        }
        .sheet(isPresented: $debugMenuShown) {
            NavigationStack {
                DebugMenuView()
            }
        }
    }

    @ViewBuilder
    private func stepView(_ step: OnboardingStep) -> some View {
        switch step {
        case .intro: IntroStep(model: model, showDemo: showDemo)
        case .invite: InviteStep(model: model, playAsGuest: showDemo)
        case .age: AgeStep(model: model)
        case .terms: TermsStep(model: model)
        case .consent: ConsentStep(model: model)
        case .signIn: SignInStep(model: model, browseWithoutSignIn: browseWithoutSignIn, playAsGuest: showDemo)
        }
    }

    /// «Отладка» — «Лаборатория» и «Проверка установки» до входа: вход ждёт Client ID, а пробная установка должна
    /// открывать проверки сразу.
    @ToolbarContentBuilder
    private var debugToolbar: some ToolbarContent {
        if DebugAccess.buildAllows {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    debugMenuShown = true
                } label: {
                    Image(systemName: "ladybug.fill")
                        .symbolRenderingMode(.multicolor)
                        .symbolEffect(.wiggle, options: .repeat(.periodic(delay: 6)))
                }
                .accessibilityLabel("Отладка")
            }
        }
    }
}

// MARK: - Шаги

/// Первый экран: карта Бреста на весь экран с живой сценой карточки (`IntroBackdrop`), сверху «ГОРОДКИ», снизу
/// стеклянная панель — текст карточки (листается), точки страниц и кнопки.
private struct IntroStep: View {
    @Bindable var model: OnboardingModel
    var showDemo: (@MainActor @Sendable () -> Void)? = nil
    @State private var headerHeight: CGFloat = 120
    @State private var panelHeight: CGFloat = 340
    @Environment(\.brandSound) private var sound
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        ZStack {
            IntroBackdrop(page: model.introPage, topInset: headerHeight, bottomInset: panelHeight)
                .ignoresSafeArea()
            // Значки, которые пересыпаются от наклона телефона.
            TiltChips(top: headerHeight + 8, bottom: panelHeight)
                .ignoresSafeArea()
            VStack(spacing: 0) {
                IntroWordmark()
                    .padding(.horizontal, 24)
                    .padding(.top, 4)
                    .onGeometryChange(for: CGFloat.self) {
                        $0.frame(in: .global).maxY
                    } action: {
                        headerHeight = $0
                    }
                Spacer(minLength: 0)
                panel
                    .onGeometryChange(for: CGFloat.self) {
                        $0.size.height
                    } action: {
                        panelHeight = $0 + 24
                    }
            }
        }
        .atmosphereBackground()
        .navigationBarTitleDisplayMode(.inline)
        .sensoryFeedback(.selection, trigger: model.introPage)
        .onChange(of: model.introPage) { sound.play(.swoosh) }
    }

    private var panel: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(IntroCard.all[model.introPage].tag)
                .font(.system(size: 12, weight: .heavy).width(.expanded))
                .foregroundStyle(accent)
                .contentTransition(.opacity)
            TabView(selection: $model.introPage) {
                ForEach(IntroCard.all) { card in
                    IntroCardView(card: card)
                        .tag(card.id)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            .frame(height: 132)
            IntroPageDots(current: model.introPage)
            VStack(spacing: 4) {
                HStack(spacing: 10) {
                    if model.introPage > 0 {
                        Button {
                            withAnimation { model.introPage -= 1 }
                        } label: {
                            Image(systemName: "chevron.left")
                                .font(.title3.weight(.bold))
                                .foregroundStyle(Palette.uiInk.color)
                                .frame(width: Metrics.controlHeight, height: Metrics.controlHeight)
                                .glassEffect(.regular.interactive(), in: .circle)
                        }
                        .accessibilityLabel("Назад")
                        .transition(.scale.combined(with: .opacity))
                    }
                    Button(model.introPage < IntroCard.all.count - 1 ? "Дальше" : "Начать") {
                        if model.introPage < IntroCard.all.count - 1 {
                            withAnimation { model.introPage += 1 }
                        } else {
                            model.advance(from: .intro)
                        }
                    }
                    .buttonStyle(.neutral)
                }
                .animation(.spring(duration: 0.4, bounce: 0.3), value: model.introPage > 0)
                HStack {
                    Button("Уже играю — войти") {
                        model.startReturning()
                    }
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .frame(minHeight: 44)
                    Spacer(minLength: 8)
                    if let showDemo {
                        Button("Посмотреть демо", systemImage: "play.circle", action: showDemo)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Palette.uiInk2.color)
                            .frame(minHeight: 44)
                            .accessibilityIdentifier("intro.demo")
                    }
                }
            }
        }
        .padding(20)
        .background {
            // Панель в цвете карточки: снизу вверх — от прозрачного к оттенку (своя земля, туман, соперник).
            LinearGradient(colors: [accent.opacity(0.30), accent.opacity(0.04)], startPoint: .top, endPoint: .bottom)
                .clipShape(.rect(cornerRadius: Radius.panel))
                .animation(.smooth(duration: 0.5), value: model.introPage)
        }
        .panelGlass()
        .overlay { ShimmerRim(color: accent, cornerRadius: Radius.panel) }
        .padding(.horizontal, 12)
        .padding(.bottom, 8)
    }

    private var accent: Color {
        IntroCard.all[model.introPage].accent(Theme(colorScheme))
    }
}

/// Кромка панели переливается: светлый блик медленно обходит её по кругу.
private struct ShimmerRim: View {
    let color: Color
    let cornerRadius: CGFloat
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { timeline in
            let angle =
                reduceMotion ? 0 : timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 6) * 60
            RoundedRectangle(cornerRadius: cornerRadius)
                .strokeBorder(
                    AngularGradient(
                        colors: [color, .white.opacity(0.9), color.opacity(0.25), color.opacity(0.6), color],
                        center: .center, angle: .degrees(angle)),
                    lineWidth: 1.6)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

private struct InviteStep: View {
    @Bindable var model: OnboardingModel
    /// Играть без кода — гостем, на данных-примерах (`DemoMode`); вернуться к коду — «Войти» на плашке гостя.
    var playAsGuest: (@MainActor @Sendable () -> Void)? = nil
    @FocusState private var focused: Bool

    var body: some View {
        StepScaffold(
            title: "Код приглашения",
            text: "Пока в игру попадают по приглашениям. Код даёт тот, кто позвал тебя играть.",
            error: model.errorMessage(on: .invite)
        ) {
            TextField("ABCD-2345", text: $model.inviteCode)
                .font(.title2.monospaced().weight(.semibold))
                .textInputAutocapitalization(.characters)
                .autocorrectionDisabled()
                .keyboardType(.asciiCapable)
                .submitLabel(.next)
                .focused($focused)
                .onSubmit { if model.inviteReady { model.advance(from: .invite) } }
                .onChange(of: model.inviteCode) { _, typed in
                    // Заглавными сразу: как код записан у того, кто его дал.
                    let upper = typed.uppercased()
                    if upper != typed { model.inviteCode = upper }
                }
                .contentCard()
                .accessibilityLabel("Код приглашения")
        } footer: {
            Button("Дальше") { model.advance(from: .invite) }
                .buttonStyle(.neutral)
                .disabled(!model.inviteReady)
            if let playAsGuest {
                GuestButton(title: "Играть без кода — гостем", action: playAsGuest)
            }
        }
    }
}

private struct AgeStep: View {
    @Bindable var model: OnboardingModel

    var body: some View {
        StepScaffold(
            title: "Тебе есть 16?",
            text: "Играть можно с 16 лет — так требует закон о защите персональных данных.",
            error: model.errorMessage(on: .age)
        ) {
            Toggle("Мне 16 лет или больше", isOn: $model.ageConfirmed)
                .toggleStyle(CheckmarkToggleStyle())
                .contentCard()
        } footer: {
            Button("Дальше") { model.advance(from: .age) }
                .buttonStyle(.neutral)
                .disabled(!model.ageConfirmed)
        }
    }
}

private struct TermsStep: View {
    @Bindable var model: OnboardingModel
    @State private var opened: LegalDocument.Name?

    var body: some View {
        StepScaffold(
            title: "Правила игры",
            text: "Пользовательское соглашение и политика: что можно в игре и как мы обращаемся с данными.",
            error: model.errorMessage(on: .terms)
        ) {
            VStack(alignment: .leading, spacing: 12) {
                Toggle("Принимаю пользовательское соглашение", isOn: $model.termsAccepted)
                    .toggleStyle(CheckmarkToggleStyle())
                HStack(spacing: 16) {
                    Button("Прочитать соглашение") { opened = .terms }
                    Button("Политика") { opened = .privacy }
                }
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
                .padding(.leading, 36)
            }
            .contentCard()
        } footer: {
            Button("Дальше") { model.advance(from: .terms) }
                .buttonStyle(.neutral)
                .disabled(!model.termsAccepted)
        }
        .sheet(item: $opened) { name in
            LegalDocumentSheet(name: name)
        }
    }
}

/// Согласие — отдельно от иной информации (статья 5 Закона, docs/legal/consent-v1.md): на экране только его текст и
/// одна отметка.
private struct ConsentStep: View {
    @Bindable var model: OnboardingModel
    private let consent = LegalDocument.load(.consent)

    var body: some View {
        StepScaffold(
            title: "Согласие на обработку данных",
            text: "Прочитай, какие данные игра обрабатывает и зачем, — и отметь согласие.",
            error: model.errorMessage(on: .consent)
        ) {
            VStack(alignment: .leading, spacing: 16) {
                // Подпись отметки — из раздела «Отметка» самого документа: без текста согласия отметки нет.
                if let consent, let checkbox = consent.checkbox {
                    LegalDocumentView(document: consent)
                    Divider()
                    Toggle(checkbox, isOn: $model.consentGiven)
                        .toggleStyle(CheckmarkToggleStyle())
                } else {
                    Text("Текста согласия нет в этой сборке.")
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            .contentCard()
        } footer: {
            Button("Дальше") { model.advance(from: .consent) }
                .buttonStyle(.neutral)
                .disabled(!model.consentReady)
        }
    }
}

private struct SignInStep: View {
    @Bindable var model: OnboardingModel
    var browseWithoutSignIn: (@MainActor @Sendable () -> Void)?
    /// Войти позже — играть гостем на данных-примерах.
    var playAsGuest: (@MainActor @Sendable () -> Void)? = nil

    var body: some View {
        StepScaffold(
            title: model.returningPlayer ? "С возвращением" : "Последний шаг",
            text: model.returningPlayer
                ? "Войди тем же аккаунтом Google, что и раньше."
                : "Вход через Google. Ник и цвет назначатся сами — поменять ник можно потом в профиле.",
            error: model.errorMessage(on: .signIn)
        ) {
            if !model.signInAvailable {
                Label("Вход через Google появится после настройки", systemImage: "clock")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .contentCard()
            }
            Label(
                "Вход через Apple — в сборке для App Store (нужен платный аккаунт разработчика)",
                systemImage: "apple.logo"
            )
            .font(.footnote)
            .foregroundStyle(Palette.uiInk2.color)
            .contentCard()
        } footer: {
            Button {
                Task { await model.signInWithGoogle() }
            } label: {
                if model.isSigningIn {
                    ProgressView()
                        .tint(Palette.uiButtonInk.color)
                } else {
                    Text("Войти через Google")
                }
            }
            .buttonStyle(.neutral)
            .disabled(!model.signInAvailable || model.isSigningIn)
            if let playAsGuest {
                GuestButton(title: "Войти позже — играть гостем", action: playAsGuest)
            }
            if let browseWithoutSignIn {
                Button("Посмотреть без входа", action: browseWithoutSignIn)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .frame(minHeight: 44)
            }
        }
    }
}

/// «Играть гостем»: стеклянная капсула со значком — вторая по важности кнопка шага.
private struct GuestButton: View {
    let title: String
    let action: @MainActor @Sendable () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: "gamecontroller.fill")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(Palette.uiInk.color)
                .frame(maxWidth: .infinity, minHeight: 50)
                .glassEffect(.regular.interactive(), in: .capsule)
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("onboarding.guest")
    }
}

/// Раскладка шага: заголовок и пояснение, содержимое с прокруткой, ошибка, кнопки внизу.
private struct StepScaffold<Content: View, Footer: View>: View {
    let title: String
    let text: String
    let error: String?
    @ViewBuilder let content: Content
    @ViewBuilder let footer: Footer

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text(title)
                    .font(.system(size: 30, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(Palette.uiInk.color)
                Text(text)
                    .font(.body)
                    .foregroundStyle(Palette.uiInk2.color)
                if let error {
                    ErrorCard(message: error)
                }
                content
            }
            .padding(20)
        }
        .scrollDismissesKeyboard(.interactively)
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 8) {
                footer
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .background(.ultraThinMaterial)
        }
        .liveGradientBackground()
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// Ошибка входа — текстом `SignInFailure.message`.
private struct ErrorCard: View {
    let message: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Palette.warn.color)
            Text(message)
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk.color)
        }
        .contentCard(padding: 14)
        .accessibilityElement(children: .combine)
    }
}
