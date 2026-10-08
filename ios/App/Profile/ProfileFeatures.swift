import DesignSystem
import SwiftUI

/// Разделы «Профиля» с пунктами листика (PLAN.md, §4 и §5): медиа, данные на телефоне и в облаке, друзья и сезоны,
/// уведомления. Карточки контентного слоя, без стекла — как строки «Отладка» и «Выйти».
struct ProfileFeatures: View {
    let model: ProfileModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            group("Медиа") {
                link("Галерея", systemImage: "photo.on.rectangle") { GalleryView() }
                divider
                link("Видео-повтор забега", systemImage: "play.rectangle") {
                    ReplayView(model: .live(player: model.playerColor))
                }
            }
            group("Данные") {
                link("Хранилище", systemImage: "internaldrive") { StorageView() }
                divider
                link("Резервная копия", systemImage: "icloud.and.arrow.up") { BackupView() }
                divider
                link("Файлы и экспорт", systemImage: "folder") { FilesView() }
            }
            group("Друзья и сезоны") {
                link("Мой QR и сканер", systemImage: "qrcode") {
                    MyQRView(playerId: model.playerId, playerName: model.displayName, player: model.playerColor)
                }
                divider
                link("Пригласить друга", systemImage: "person.badge.plus") {
                    InviteView(model: .live(senderName: model.displayName))
                }
                divider
                link("Календарь сезонов", systemImage: "calendar") { CalendarView() }
            }
            group("Настройки") {
                link("Уведомления", systemImage: "bell.badge") { NotificationsView() }
            }
        }
    }

    private var divider: some View {
        Divider().padding(.leading, 52)
    }

    private func group<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Palette.uiInk2.color)
                .padding(.leading, 12)
            VStack(spacing: 0) {
                content()
            }
            .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
        }
    }

    private func link<Destination: View>(
        _ title: String, systemImage: String, @ViewBuilder destination: @escaping () -> Destination
    ) -> some View {
        NavigationLink {
            destination()
        } label: {
            ProfileRow(title: title, systemImage: systemImage)
        }
        .buttonStyle(.plain)
    }
}
