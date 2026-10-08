import CoreImage
import CoreImage.CIFilterBuiltins
import DesignSystem
import GameCore
import SwiftUI
import UIKit

/// QR-код из текста (CoreImage, `CIQRCodeGenerator`): чёрные модули на белом, без сглаживания — так его читает любая
/// камера.
enum QRCodeImage {
    static func make(_ text: String, scale: CGFloat = 12) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: scale, y: scale)),
            let image = CIContext().createCGImage(output, from: output.extent)
        else { return nil }
        return UIImage(cgImage: image)
    }
}

/// «Мой QR» (пункт 13 листика): свой код друга — или код приглашения, если он записан в «Пригласить друга»; отсюда же —
/// своя камера со сканером. Карточка появляется с мягким бликом (один проход, при «Уменьшить движение» — без него).
struct MyQRView: View {
    enum Content: String, CaseIterable, Identifiable {
        case player, invite
        var id: String { rawValue }
    }

    let playerId: String?
    let playerName: String?
    let player: PlayerColor
    var inviteCode: String?
    @State private var content = Content.player
    @State private var glint = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(playerId: String?, playerName: String?, player: PlayerColor, inviteCode: String? = nil) {
        self.playerId = playerId
        self.playerName = playerName
        self.player = player
        let saved = inviteCode ?? UserDefaults.standard.string(forKey: InviteModel.codeKey)
        self.inviteCode = saved.map(InviteText.normalizedCode).flatMap { InviteText.isWellFormed($0) ? $0 : nil }
    }

    private var link: FriendLink? {
        switch content {
        case .player: playerId.map { FriendLink.player(id: $0, name: playerName) }
        case .invite: inviteCode.map { FriendLink.invite(code: $0) }
        }
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                if inviteCode != nil {
                    Picker("Что в коде", selection: $content) {
                        Text("Мой профиль").tag(Content.player)
                        Text("Приглашение").tag(Content.invite)
                    }
                    .pickerStyle(.segmented)
                }
                card
                NavigationLink {
                    ScannerView()
                } label: {
                    Label("Сканировать код друга", systemImage: "qrcode.viewfinder")
                }
                .buttonStyle(.neutral)
                if let link {
                    ShareLink(item: link.url) {
                        Label("Поделиться ссылкой", systemImage: "square.and.arrow.up")
                            .font(.headline)
                            .foregroundStyle(Palette.uiInk.color)
                    }
                }
            }
            .padding(20)
        }
        .background(Palette.uiBackground.color)
        .navigationTitle("Мой QR")
        .onAppear {
            guard !reduceMotion else { return }
            // Блик — как металлический блик значка (docs/design/tokens.md, §7), один раз.
            let duration = MotionSpec.BadgeDrop.glintEnd - MotionSpec.BadgeDrop.glintStart
            withAnimation(.easeInOut(duration: duration).delay(0.3)) { glint = true }
        }
    }

    private var card: some View {
        VStack(spacing: 16) {
            HStack(spacing: 12) {
                Text(String((playerName ?? "?").prefix(1)))
                    .font(.headline.bold())
                    .fontDesign(.rounded)
                    .foregroundStyle(player.startInkColor)
                    .frame(width: 44, height: 44)
                    .background(player.color, in: .circle)
                    .overlay { Circle().stroke(player.edgeColor, lineWidth: 2).padding(-3) }
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(playerName ?? "Игрок")
                        .font(.headline)
                        .foregroundStyle(Palette.uiInk.color)
                    Text(content == .invite ? "Код приглашения \(inviteCode ?? "")" : "Друг в «Городках»")
                        .font(.subheadline)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                Spacer()
            }
            qr
            Text("Друг открывает «Профиль → Мой QR → Сканировать» и наводит камеру.")
                .font(.footnote)
                .foregroundStyle(Palette.uiInk2.color)
                .multilineTextAlignment(.center)
        }
        .padding(20)
        .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
    }

    @ViewBuilder
    private var qr: some View {
        if let link, let image = QRCodeImage.make(link.url) {
            Image(uiImage: image)
                .interpolation(.none)
                .resizable()
                .scaledToFit()
                .padding(14)
                .background(.white, in: .rect(cornerRadius: 20))
                .overlay { glintBand }
                .clipShape(.rect(cornerRadius: 20))
                .frame(maxWidth: 260)
                .accessibilityLabel("QR-код")
        } else {
            Text("Код появится после входа: в нём номер игрока с сервера.")
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
                .frame(maxWidth: .infinity, minHeight: 200)
                .background(Palette.uiCell2.color, in: .rect(cornerRadius: 20))
        }
    }

    /// Блик — светлая полоса под углом, один проход по карточке при появлении.
    private var glintBand: some View {
        GeometryReader { proxy in
            LinearGradient(
                colors: [.white.opacity(0), .white.opacity(0.75), .white.opacity(0)], startPoint: .leading,
                endPoint: .trailing
            )
            .frame(width: proxy.size.width * 0.45)
            .rotationEffect(.degrees(20))
            .offset(x: glint ? proxy.size.width * 1.3 : -proxy.size.width * 0.8)
            .blendMode(.plusLighter)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}
