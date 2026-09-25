import DesignSystem
import Foundation
import SwiftUI

/// Соглашение, политика и согласие (docs/legal) — ресурсы приложения (`project.yml`): приложение показывает сами
/// файлы, как есть, с отметками черновика — второй копии текста, которая разошлась бы с документом, нет. Номер версии
/// в имени файла меняется вместе с `SignInService.consentVersion` (docs/legal/README.md, «Как выпустить версию 2»).
struct LegalDocument: Equatable {
    enum Name: String, CaseIterable, Identifiable, Sendable {
        case terms = "terms-v1"
        case privacy = "privacy-v1"
        case consent = "consent-v1"

        var id: String { rawValue }

        var title: String {
            switch self {
            case .terms: "Соглашение"
            case .privacy: "Политика"
            case .consent: "Согласие"
            }
        }

        /// Ссылка между документами: «[политики](privacy-v1.md)». Остальные ссылки (README, PLAN) — не для игрока.
        init?(link: URL) {
            let file = link.lastPathComponent
            guard file.hasSuffix(".md") else { return nil }
            self.init(rawValue: String(file.dropLast(".md".count)))
        }
    }

    /// Куски разметки, которые умеет экран: заголовки, абзацы, списки, цитата (плашка «ЧЕРНОВИК») и строки таблицы.
    enum Block: Equatable {
        case title(String)
        case heading(String)
        case paragraph(String)
        case bullet(String, level: Int)
        /// Строки цитаты; пункты списка внутри — с «• ».
        case quote([String])
        /// Строка таблицы и заголовки её столбцов.
        case row(cells: [String], header: [String])
    }

    var blocks: [Block]

    /// Документ из ресурсов приложения; `nil` — файла нет в сборке.
    static func load(_ name: Name, bundle: Bundle = .main) -> LegalDocument? {
        guard let url = bundle.url(forResource: name.rawValue, withExtension: "md"),
            let text = try? String(contentsOf: url, encoding: .utf8)
        else { return nil }
        // Отметку согласия экран рисует сам — флажком под текстом.
        return parse(text, stopAt: name == .consent ? "## Отметка" : nil)
    }

    /// Разбор Markdown документов docs/legal. `stopAt` — строка, с которой текст дальше не нужен.
    static func parse(_ markdown: String, stopAt: String? = nil) -> LegalDocument {
        var blocks: [Block] = []
        var paragraph: [String] = []
        var quote: [String] = []
        var header: [String]?

        func flush() {
            if !paragraph.isEmpty {
                blocks.append(.paragraph(paragraph.joined(separator: " ")))
                paragraph = []
            }
            if !quote.isEmpty {
                blocks.append(.quote(quote))
                quote = []
            }
        }

        for rawLine in markdown.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if let stopAt, line == stopAt { break }
            if line.isEmpty {
                flush()
                header = nil
                continue
            }
            if line.hasPrefix(">") {
                if !paragraph.isEmpty { flush() }
                let text = line.dropFirst().trimmingCharacters(in: .whitespaces)
                guard !text.isEmpty else { continue }
                if text.hasPrefix("- ") {
                    quote.append("• " + text.dropFirst(2))
                } else if rawLine.hasPrefix(">  "), let last = quote.popLast() {
                    quote.append(last + " " + text)  // продолжение пункта цитаты на следующей строке
                } else {
                    quote.append(text)
                }
                continue
            }
            if line.hasPrefix("|") {
                flush()
                let cells = line.trimmingCharacters(in: CharacterSet(charactersIn: "|")).components(separatedBy: "|")
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                if cells.allSatisfy({ $0.allSatisfy { $0 == "-" || $0 == ":" } }) {
                    continue  // |---|---|
                }
                if let header {
                    blocks.append(.row(cells: cells, header: header))
                } else {
                    header = cells
                }
                continue
            }
            if line.hasPrefix("# ") {
                flush()
                blocks.append(.title(String(line.dropFirst(2))))
            } else if line.hasPrefix("## ") || line.hasPrefix("### ") {
                flush()
                blocks.append(.heading(line.drop(while: { $0 == "#" }).trimmingCharacters(in: .whitespaces)))
            } else if line.hasPrefix("- ") || line.hasPrefix("* ") || isNumbered(line) {
                flush()
                let indent = rawLine.prefix(while: { $0 == " " }).count
                let text = line.hasPrefix("- ") || line.hasPrefix("* ") ? String(line.dropFirst(2)) : line
                blocks.append(.bullet(text, level: indent / 2))
            } else if rawLine.hasPrefix(" "), case .bullet(let text, let level) = blocks.last, paragraph.isEmpty {
                blocks[blocks.count - 1] = .bullet(text + " " + line, level: level)  // продолжение пункта
            } else {
                paragraph.append(line)
            }
        }
        flush()
        return LegalDocument(blocks: blocks)
    }

    /// «1. «Мне 16 лет или больше»» — пункт нумерованного списка.
    private static func isNumbered(_ line: String) -> Bool {
        let digits = line.prefix(while: \.isNumber)
        return !digits.isEmpty && line.dropFirst(digits.count).hasPrefix(". ")
    }
}

/// Документ на экране: текст как есть, ссылки на соседние документы открываются листом поверх.
struct LegalDocumentView: View {
    let document: LegalDocument
    @State private var opened: LegalDocument.Name?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(Array(document.blocks.enumerated()), id: \.offset) { _, block in
                BlockView(block: block)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .environment(
            \.openURL,
            OpenURLAction { url in
                guard let name = LegalDocument.Name(link: url) else { return .discarded }
                opened = name
                return .handled
            }
        )
        .sheet(item: $opened) { name in
            LegalDocumentSheet(name: name)
        }
    }
}

/// Документ целиком — листом: «Прочитать соглашение», ссылки внутри документов.
struct LegalDocumentSheet: View {
    let name: LegalDocument.Name
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ScrollView {
                if let document = LegalDocument.load(name) {
                    LegalDocumentView(document: document)
                        .padding(20)
                } else {
                    Text("Текста нет в этой сборке.")
                        .foregroundStyle(Palette.uiInk2.color)
                        .padding(20)
                }
            }
            .background(Palette.uiBackground.color)
            .navigationTitle(name.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Готово") { dismiss() }
                }
            }
        }
    }
}

private struct BlockView: View {
    let block: LegalDocument.Block

    var body: some View {
        switch block {
        case .title(let text):
            Text(Self.inline(text))
                .font(.title2.bold())
                .foregroundStyle(Palette.uiInk.color)
        case .heading(let text):
            Text(Self.inline(text))
                .font(.headline)
                .foregroundStyle(Palette.uiInk.color)
                .padding(.top, 8)
        case .paragraph(let text):
            Text(Self.inline(text))
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk.color)
        case .bullet(let text, let level):
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(level == 0 ? "•" : "◦")
                Text(Self.inline(text))
            }
            .font(.subheadline)
            .foregroundStyle(Palette.uiInk.color)
            .padding(.leading, CGFloat(level) * 16)
        case .quote(let lines):
            VStack(alignment: .leading, spacing: 6) {
                ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                    Text(Self.inline(line))
                }
            }
            .font(.footnote)
            .foregroundStyle(Palette.uiInk2.color)
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Palette.uiCell2.color, in: .rect(cornerRadius: Radius.miniProgress))
        case .row(let cells, let header):
            VStack(alignment: .leading, spacing: 4) {
                if let first = cells.first {
                    Text(Self.inline(first))
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk.color)
                }
                if cells.count > 1 {
                    Text(Self.inline(cells[1]))
                        .font(.subheadline)
                        .foregroundStyle(Palette.uiInk.color)
                }
                ForEach(Array(cells.dropFirst(2).enumerated()), id: \.offset) { index, cell in
                    let title = header.indices.contains(index + 2) ? header[index + 2] + ": " : ""
                    Text(Self.inline(title + cell))
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            .padding(.vertical, 4)
        }
    }

    /// Жирный, код и ссылки внутри строки; не разобралось — строка как есть.
    static func inline(_ text: String) -> AttributedString {
        (try? AttributedString(
            markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}
