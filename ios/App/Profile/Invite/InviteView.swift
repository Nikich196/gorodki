import Contacts
import ContactsUI
import DesignSystem
import GameCore
import SwiftUI

/// Друг из Контактов — только имя и телефон того, кого игрок выбрал сам.
struct FriendContact: Equatable, Sendable {
    var name: String
    var phone: String?
}

/// Контакты (пункт 7 листика; PLAN.md, §6.6: «через `ContactAccessButton`»): приложение видит только тех, кого игрок
/// отметил кнопкой доступа, — остальная книга закрыта.
@MainActor
protocol ContactsReading {
    func contacts(withIdentifiers identifiers: [String]) async -> [FriendContact]
}

struct SystemContacts: ContactsReading {
    func contacts(withIdentifiers identifiers: [String]) async -> [FriendContact] {
        await Self.fetch(identifiers)
    }

    /// `CNContactStore` не `Sendable` — живёт внутри одной функции.
    @concurrent
    private static func fetch(_ identifiers: [String]) async -> [FriendContact] {
        let keys: [CNKeyDescriptor] = [
            CNContactGivenNameKey as CNKeyDescriptor, CNContactFamilyNameKey as CNKeyDescriptor,
            CNContactPhoneNumbersKey as CNKeyDescriptor,
        ]
        let predicate = CNContact.predicateForContacts(withIdentifiers: identifiers)
        let found = (try? CNContactStore().unifiedContacts(matching: predicate, keysToFetch: keys)) ?? []
        return found.map { contact in
            let name = [contact.givenName, contact.familyName].filter { !$0.isEmpty }.joined(separator: " ")
            return FriendContact(name: name, phone: contact.phoneNumbers.first?.value.stringValue)
        }
    }
}

/// «Пригласить друга»: код приглашения (его выдаёт админ, игрок вводит один раз), друг из Контактов кнопкой доступа,
/// сообщение — SMS или «Поделиться» в любой мессенджер.
@MainActor
@Observable
final class InviteModel {
    static let codeKey = "invite.code"

    var inviteCode: String {
        didSet { defaults.set(inviteCode, forKey: Self.codeKey) }
    }
    var friendQuery = ""
    var friend: FriendContact?
    var senderName: String?

    @ObservationIgnored let contacts: any ContactsReading
    @ObservationIgnored let defaults: UserDefaults

    init(contacts: any ContactsReading, defaults: UserDefaults = .standard, senderName: String? = nil) {
        self.contacts = contacts
        self.defaults = defaults
        self.senderName = senderName
        inviteCode = defaults.string(forKey: Self.codeKey) ?? ""
    }

    static func live(senderName: String? = nil) -> InviteModel {
        InviteModel(contacts: SystemContacts(), senderName: senderName)
    }

    var codeIsValid: Bool { InviteText.isWellFormed(InviteText.normalizedCode(inviteCode)) }

    var message: String {
        InviteText.message(
            friendName: friend?.name.split(separator: " ").first.map(String.init), inviteCode: inviteCode,
            senderName: senderName)
    }

    /// SMS выбранному другу с готовым текстом; `nil` — у друга нет телефона.
    var smsURL: URL? {
        guard let phone = friend?.phone else { return nil }
        let digits = phone.filter { $0.isNumber || $0 == "+" }
        var components = URLComponents()
        components.scheme = "sms"
        components.path = digits
        guard let base = components.string,
            let body = message.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed.subtracting(["&", "=", "+"]))
        else { return nil }
        return URL(string: base + "&body=" + body)
    }

    /// Игрок отметил друзей кнопкой доступа — взять первого.
    func picked(_ identifiers: [String]) async {
        guard let first = await contacts.contacts(withIdentifiers: identifiers).first else { return }
        friend = first
        friendQuery = ""
    }
}

struct InviteView: View {
    @State private var model: InviteModel

    init(model: InviteModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        TokenList {
            Section {
                SheetIntro(
                    systemImage: "person.badge.plus", title: "Позови друга",
                    text: "Регистрация — по кодам приглашения. Впиши свой код, выбери друга — сообщение готово.")
            }
            Section {
                TextField("ABCD-2345", text: Bindable(model).inviteCode)
                    .textInputAutocapitalization(.characters)
                    .autocorrectionDisabled()
                    .font(.body.monospaced())
            } header: {
                Text("Код приглашения")
            } footer: {
                Text(
                    model.inviteCode.isEmpty || model.codeIsValid
                        ? "Коды выдаёт организатор сезона; игра их не создаёт." : "Код — две группы по четыре знака.")
            }
            friendSection
            Section {
                Text(model.message)
                    .font(.callout)
                    .foregroundStyle(Palette.uiInk.color)
                    .padding(12)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Palette.uiCell2.color, in: .rect(cornerRadius: 16))
                if let sms = model.smsURL {
                    Link(destination: sms) {
                        Label("Отправить SMS", systemImage: "message")
                    }
                }
                ShareLink(item: model.message) {
                    Label("Поделиться приглашением", systemImage: "square.and.arrow.up")
                }
            } header: {
                Text("Сообщение")
            }
        }
        .tint(Palette.uiInk.color)
        .navigationTitle("Пригласить друга")
    }

    private var friendSection: some View {
        Section {
            if let friend = model.friend {
                HStack(spacing: 12) {
                    Text(String(friend.name.prefix(1)))
                        .font(.headline)
                        .foregroundStyle(Palette.uiButtonInk.color)
                        .frame(width: 40, height: 40)
                        .background(Palette.uiButton.color, in: .circle)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(friend.name)
                            .foregroundStyle(Palette.uiInk.color)
                        Text(friend.phone ?? "без телефона — через «Поделиться»")
                            .font(.caption)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    Spacer()
                    Button("Другой") { model.friend = nil }
                        .buttonStyle(.borderless)
                }
            } else {
                TextField("Имя друга в Контактах", text: Bindable(model).friendQuery)
                    .autocorrectionDisabled()
                ContactAccessButton(queryString: model.friendQuery) { identifiers in
                    Task { await model.picked(identifiers) }
                }
                .frame(minHeight: 44)
            }
        } header: {
            Text("Друг")
        } footer: {
            Text("Игра видит только тех, кого ты отметишь, — остальные Контакты закрыты.")
        }
    }
}
