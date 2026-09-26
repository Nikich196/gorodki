import DesignSystem
import SwiftUI
import UIKit

// Общие кусочки социальных экранов: контентный слой без стекла (docs/design/tokens.md, §6, п. 2) — `ui-cell`
// на `ui-bg`, цвета только из токенов.

/// Экран без данных: не вышло загрузить (текст — `SocialFailure.message`) и «повторить».
struct SocialFailureView: View {
    let failure: SocialFailure
    let retry: () async -> Void

    var body: some View {
        VStack(spacing: 0) {
            TabPlaceholder(systemImage: symbolName, title: title, text: failure.message)
            Button("Повторить") {
                Task { await retry() }
            }
            .buttonStyle(.neutral)
            .padding(.horizontal, 32)
            .padding(.bottom, 32)
            .background(Palette.uiBackground.color)
        }
    }

    private var symbolName: String {
        switch failure {
        case .notReady: "hammer"
        case .offline: "wifi.slash"
        case .signedOut: "person.crop.circle.badge.questionmark"
        case .rejected, .unexpected: "exclamationmark.triangle"
        }
    }

    private var title: String {
        switch failure {
        case .notReady: "Скоро"
        case .offline: "Нет связи"
        case .signedOut: "Нужен вход"
        case .rejected, .unexpected: "Не получилось"
        }
    }
}

/// Кружок игрока: цвет палитры, первая буква ника цветом текста «Старта», кромка темы (как шапка «Профиля»).
struct PlayerAvatar: View {
    let name: String
    let color: PlayerColor
    var size: CGFloat = 40

    var body: some View {
        Text(String(name.first.map(String.init) ?? "?"))
            .font(.system(size: size * 0.42, weight: .bold, design: .rounded))
            .foregroundStyle(color.startInkColor)
            .frame(width: size, height: size)
            .background(color.color, in: .circle)
            .overlay { Circle().stroke(color.edgeColor, lineWidth: 2) }
            .accessibilityHidden(true)
    }
}

/// Строка-сообщение над списком: «Жалоба отправлена», ошибка действия. Закрывается касанием.
struct NoticeBanner: View {
    @Binding var text: String?

    var body: some View {
        if let text {
            Button {
                self.text = nil
            } label: {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Image(systemName: "info.circle")
                        .accessibilityHidden(true)
                    Text(verbatim: text)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    Image(systemName: "xmark")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Palette.uiInk3.color)
                        .accessibilityLabel("Закрыть")
                }
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk.color)
                .contentCard(padding: 14)
            }
            .buttonStyle(.plain)
            .transition(.opacity)
        }
    }
}

/// Код для друга или клана: крупно, моноширинно, «Скопировать» и «Поделиться» (`ShareLink`). QR-код и сканер —
/// в «Моём QR» листика (lab2); до него — только текст.
struct ShareCodeCard: View {
    let title: String
    let code: String
    /// Текст для «Поделиться»: код и где его ввести.
    let shareText: String
    var footnote: String?
    @State private var copied = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
            Text(verbatim: code)
                .font(.system(size: 34, weight: .heavy, design: .monospaced))
                .foregroundStyle(Palette.uiInk.color)
                .textSelection(.enabled)
                .contentTransition(.numericText())
                .accessibilityLabel(Text(verbatim: code.map(String.init).joined(separator: " ")))
            HStack(spacing: 10) {
                Button {
                    UIPasteboard.general.string = code
                    copied += 1
                } label: {
                    Label(
                        copied > 0 ? "Скопировано" : "Скопировать", systemImage: copied > 0 ? "checkmark" : "doc.on.doc"
                    )
                    .contentTransition(.symbolEffect(.replace))
                }
                ShareLink(item: shareText) {
                    Label("Поделиться", systemImage: "square.and.arrow.up")
                }
            }
            .buttonStyle(.bordered)
            .tint(Palette.uiInk.color)
            .sensoryFeedback(.success, trigger: copied)
            if let footnote {
                Text(footnote)
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        }
        .contentCard()
    }
}

/// Поле кода (клан, друг): заглавными, без автоисправления, моноширинно.
struct CodeField: View {
    let prompt: String
    @Binding var text: String

    var body: some View {
        TextField(prompt, text: $text)
            .font(.system(.title3, design: .monospaced).weight(.semibold))
            .textInputAutocapitalization(.characters)
            .autocorrectionDisabled()
            .submitLabel(.done)
            .padding(.horizontal, 16)
            .frame(minHeight: 52)
            .background(Palette.uiCell2.color, in: .rect(cornerRadius: Radius.plaque))
    }
}

/// Заголовок блока над карточкой — как в системных списках.
struct SectionTitle: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .font(.footnote.weight(.semibold))
            .textCase(.uppercase)
            .foregroundStyle(Palette.uiInk2.color)
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}
