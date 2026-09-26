import SwiftUI

/// Экран или карточка без данных — один компонент на все вкладки (PLAN.md, §5, экран 27 «Пустые, офлайн и ошибочные
/// состояния»): пусто («Забегов пока нет»), нет сети, сервер недоступен, ошибка, «скоро». Контентный слой — без стекла
/// (docs/design/tokens.md, §6): значок в плитке `Palette.uiCell`, заголовок, текст, по желанию — «Повторить».
///
/// Движение — только системное: значок один раз подпрыгивает (`symbolEffect(.bounce)`) после каждого «Повторить»,
/// которое снова не удалось; при «Уменьшить движение» — стоит. Бесконечных пульсаций нет (tokens.md, §7).
public struct ContentStateView: View {
    /// `page` — на весь экран (вкладка, пустой список); `card` — карточка среди контента (профиль, настройки).
    public enum Style: Sendable {
        case page, card
    }

    private let title: String
    private let systemImage: String
    private let message: String
    private let style: Style
    private let retryTitle: String
    private let retry: (() -> Void)?
    /// Сколько раз уже повторяли: меняется — значок подпрыгивает.
    private let attempt: Int
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init(
        _ title: String, systemImage: String, message: String, style: Style = .page, attempt: Int = 0,
        retryTitle: String = "Повторить", retry: (() -> Void)? = nil
    ) {
        self.title = title
        self.systemImage = systemImage
        self.message = message
        self.style = style
        self.attempt = attempt
        self.retryTitle = retryTitle
        self.retry = retry
    }

    public var body: some View {
        switch style {
        case .page:
            VStack(spacing: 14) {
                icon(size: 44, tile: 96)
                Text(title)
                    .font(.title2.bold())
                    .fontDesign(.rounded)
                    .foregroundStyle(Palette.uiInk.color)
                    .multilineTextAlignment(.center)
                Text(message)
                    .font(.body)
                    .foregroundStyle(Palette.uiInk2.color)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                if let retry {
                    Button(retryTitle, action: retry)
                        .buttonStyle(.neutral)
                        .frame(maxWidth: 240)
                        .padding(.top, 6)
                }
            }
            .padding(32)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .accessibilityElement(children: .contain)
        case .card:
            HStack(alignment: .top, spacing: 14) {
                icon(size: 22, tile: 44)
                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(.headline)
                        .foregroundStyle(Palette.uiInk.color)
                    Text(message)
                        .font(.subheadline)
                        .foregroundStyle(Palette.uiInk2.color)
                        .fixedSize(horizontal: false, vertical: true)
                    if let retry {
                        Button(retryTitle, action: retry)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Palette.uiInk.color)
                            .frame(minHeight: 44)
                    }
                }
                Spacer(minLength: 0)
            }
            .contentCard()
            .accessibilityElement(children: .contain)
        }
    }

    private func icon(size: CGFloat, tile: CGFloat) -> some View {
        Image(systemName: systemImage)
            .font(.system(size: size, weight: .semibold))
            .foregroundStyle(Palette.uiInk3.color)
            .symbolEffect(.bounce, value: reduceMotion ? 0 : attempt)
            .frame(width: tile, height: tile)
            .background(
                style == .page ? Palette.uiCell.color : Palette.uiCell2.color,
                in: .rect(cornerRadius: style == .page ? Radius.tile : Radius.miniProgress)
            )
            .accessibilityHidden(true)
    }
}

/// Загрузка без данных: системный индикатор и строка — тот же контентный слой.
public struct ContentLoadingView: View {
    private let title: String

    public init(_ title: String = "Загружаю…") {
        self.title = title
    }

    public var body: some View {
        VStack(spacing: 12) {
            ProgressView()
                .controlSize(.large)
            Text(title)
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
