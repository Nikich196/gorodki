import DesignSystem
import Networking
import SwiftUI

/// Онбординг до входа (PLAN.md, §3.18): интро → код приглашения → 16+ → соглашение и согласие → Google. Контентный
/// слой — без стекла; главная кнопка — нейтральная (`.neutral`), цвет игрока появится только на «Старте».
struct OnboardingView: View {
    @Bindable var model: OnboardingModel
    /// Посмотреть вкладки без входа — только в сборках команды (`DebugAccess.buildAllows`).
    var browseWithoutSignIn: (() -> Void)?
    @State private var debugMenuShown = false

    var body: some View {
        NavigationStack(path: $model.path) {
            IntroStep(model: model)
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
        case .intro: IntroStep(model: model)
        case .invite: InviteStep(model: model)
        case .age: AgeStep(model: model)
        case .consent: ConsentStep(model: model)
        case .signIn: SignInStep(model: model, browseWithoutSignIn: browseWithoutSignIn)
        }
    }

    /// «Отладка» — «Лаборатория» и «Проверка установки» до входа: вход ждёт Client ID, а пробная установка должна
    /// открывать проверки сразу.
    @ToolbarContentBuilder
    private var debugToolbar: some ToolbarContent {
        if DebugAccess.buildAllows {
            ToolbarItem(placement: .topBarTrailing) {
                Button("Отладка", systemImage: "ladybug") {
                    debugMenuShown = true
                }
            }
        }
    }
}

// MARK: - Шаги

private struct IntroStep: View {
    @Bindable var model: OnboardingModel

    var body: some View {
        VStack(spacing: 0) {
            TabView(selection: $model.introPage) {
                ForEach(IntroCard.all) { card in
                    IntroCardView(card: card)
                        .tag(card.id)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .always))
            .indexViewStyle(.page(backgroundDisplayMode: .always))
            VStack(spacing: 12) {
                Button(model.introPage < IntroCard.all.count - 1 ? "Дальше" : "Начать") {
                    if model.introPage < IntroCard.all.count - 1 {
                        withAnimation { model.introPage += 1 }
                    } else {
                        model.advance(from: .intro)
                    }
                }
                .buttonStyle(.neutral)
                Button("Уже играю — войти") {
                    model.startReturning()
                }
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
                .frame(minHeight: 44)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 12)
        }
        .background(Palette.uiBackground.color)
        .navigationTitle("Городки")
        .navigationBarTitleDisplayMode(.inline)
    }
}

private struct InviteStep: View {
    @Bindable var model: OnboardingModel
    @FocusState private var focused: Bool

    var body: some View {
        StepScaffold(
            title: "Код приглашения",
            text: "Пока в игру попадают по приглашениям. Код даёт тот, кто позвал тебя играть.",
            error: model.errorMessage
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
        }
    }
}

private struct AgeStep: View {
    @Bindable var model: OnboardingModel

    var body: some View {
        StepScaffold(
            title: "Тебе есть 16?",
            text: "Играть можно с 16 лет — так требует закон о защите персональных данных.",
            error: nil
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

private struct ConsentStep: View {
    @Bindable var model: OnboardingModel
    @State private var opened: LegalDocument.Name?
    private let consent = LegalDocument.load(.consent)

    var body: some View {
        StepScaffold(
            title: "Правила и согласие",
            text: "Две отдельные отметки: принять соглашение и дать согласие на обработку данных.",
            error: nil
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

            VStack(alignment: .leading, spacing: 16) {
                if let consent {
                    LegalDocumentView(document: consent)
                } else {
                    Text("Текста согласия нет в этой сборке.")
                        .foregroundStyle(Palette.uiInk2.color)
                }
                Divider()
                Toggle(
                    "Даю согласие на обработку моих персональных данных в целях, объёме и на срок, указанных выше.",
                    isOn: $model.consentGiven
                )
                .toggleStyle(CheckmarkToggleStyle())
            }
            .contentCard()
        } footer: {
            Button("Дальше") { model.advance(from: .consent) }
                .buttonStyle(.neutral)
                .disabled(!model.consentReady)
        }
        .sheet(item: $opened) { name in
            LegalDocumentSheet(name: name)
        }
    }
}

private struct SignInStep: View {
    @Bindable var model: OnboardingModel
    var browseWithoutSignIn: (() -> Void)?

    var body: some View {
        StepScaffold(
            title: model.returningPlayer ? "С возвращением" : "Последний шаг",
            text: model.returningPlayer
                ? "Войди тем же аккаунтом Google, что и раньше."
                : "Вход через Google. Ник и цвет назначатся сами — поменять ник можно потом в профиле.",
            error: model.errorMessage
        ) {
            if !model.signInAvailable {
                Label("Вход через Google появится после настройки", systemImage: "clock")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .contentCard()
            }
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
            if let browseWithoutSignIn {
                Button("Посмотреть без входа", action: browseWithoutSignIn)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .frame(minHeight: 44)
            }
        }
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
                    .font(.largeTitle.bold())
                    .fontDesign(.rounded)
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
            .background(Palette.uiBackground.color)
        }
        .background(Palette.uiBackground.color)
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
