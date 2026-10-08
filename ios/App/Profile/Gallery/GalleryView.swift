import AVKit
import DesignSystem
import GameCore
import SwiftUI

/// «Галерея» (пункт 3 листика: медиатека; PLAN.md, §5, экран 22): фото и видео из «Фото», фильтры «Все / С геотегом /
/// Видео», миниатюры с кэшем (`PHCachingImageManager` + окно `PreheatWindow`), ограниченный доступ с «Выбрать ещё».
/// Разрешение спрашивается здесь, по кнопке (PLAN.md, §6.6: «Фото — в Галерее»).
@MainActor
@Observable
final class GalleryModel {
    var access: PhotoAccess
    var filter = GalleryFilter.all
    var items: [GalleryItem] = []
    var loaded = false

    @ObservationIgnored let library: any PhotoLibraryProviding
    /// Видимые плитки — по ним окно кэша (`PreheatWindow`).
    @ObservationIgnored private var visible: Set<Int> = []
    @ObservationIgnored private var window = PreheatWindow()
    /// Размер миниатюры в пикселях: плитка ≈ треть ширины экрана × масштаб.
    static let thumbnailSize = CGSize(width: 300, height: 300)
    /// Запас плиток с каждой стороны видимых — примерно два экрана сетки.
    static let preheatMargin = 36

    init(library: any PhotoLibraryProviding) {
        self.library = library
        access = library.access()
    }

    static func live() -> GalleryModel { GalleryModel(library: SystemPhotoLibrary()) }

    func load() async {
        if access.canRead {
            let fresh = await library.items(filter)
            resetCache()
            items = fresh
        }
        loaded = true
    }

    func requestAccess() async {
        access = await library.requestAccess()
        await load()
    }

    func select(_ filter: GalleryFilter) async {
        guard filter != self.filter else { return }
        self.filter = filter
        await load()
    }

    func manageLimitedSelection() async {
        await library.manageLimitedSelection()
        access = library.access()
        await load()
    }

    func appeared(_ index: Int) {
        visible.insert(index)
        updateCache()
    }

    func disappeared(_ index: Int) {
        visible.remove(index)
        updateCache()
    }

    private func updateCache() {
        let range = visible.min().flatMap { low in visible.max().map { low...$0 } }
        let change = window.update(visible: range, total: items.count, margin: Self.preheatMargin)
        let pick = { (indices: [Int]) in indices.compactMap { self.items.indices.contains($0) ? self.items[$0] : nil } }
        if !change.start.isEmpty { library.startCaching(pick(change.start), size: Self.thumbnailSize) }
        if !change.stop.isEmpty { library.stopCaching(pick(change.stop), size: Self.thumbnailSize) }
    }

    private func resetCache() {
        let change = window.update(visible: nil, total: 0, margin: 0)
        let old = change.stop.compactMap { items.indices.contains($0) ? items[$0] : nil }
        if !old.isEmpty { library.stopCaching(old, size: Self.thumbnailSize) }
        visible = []
    }
}

struct GalleryView: View {
    @State private var model: GalleryModel
    @Namespace private var zoom
    @Environment(\.openURL) private var openURL
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private let columns = [GridItem(.adaptive(minimum: 104), spacing: 3)]

    init(model: GalleryModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Picker(
                    "Фильтр",
                    selection: Binding(
                        get: { model.filter },
                        set: { filter in
                            Task { await model.select(filter) }
                        })
                ) {
                    ForEach(GalleryFilter.allCases) { filter in
                        Text(filter.title).tag(filter)
                    }
                }
                .pickerStyle(.segmented)
                .padding(.horizontal, 16)
                if model.access == .limited {
                    limitedBanner
                }
                content
            }
            .padding(.vertical, 12)
        }
        .background(Palette.uiBackground.color)
        .navigationTitle("Галерея")
        .navigationDestination(for: GalleryItem.self) { item in
            GalleryDetailView(item: item, library: model.library)
                .navigationTransition(.zoom(sourceID: item.id, in: zoom))
        }
        .task {
            if !model.loaded { await model.load() }
        }
    }

    @ViewBuilder
    private var content: some View {
        switch model.access {
        case .notDetermined:
            permissionCard(
                title: "Твои фото с забегов", text: "Галерея показывает фото и видео из «Фото» — с геотегом и без.",
                button: "Открыть доступ к «Фото»"
            ) {
                Task { await model.requestAccess() }
            }
        case .denied:
            permissionCard(
                title: "Нет доступа к «Фото»", text: "Разрешить можно в Настройках — все фото или только выбранные.",
                button: "Открыть Настройки"
            ) {
                if let url = SystemSettings.appURL { openURL(url) }
            }
        case .limited, .full:
            if model.items.isEmpty && model.loaded {
                TabPlaceholder(
                    systemImage: model.filter.emptySymbol, title: model.filter.emptyTitle, text: model.filter.emptyText
                )
                .frame(minHeight: 320)
            } else {
                grid
            }
        }
    }

    private var grid: some View {
        LazyVGrid(columns: columns, spacing: 3) {
            ForEach(Array(model.items.enumerated()), id: \.element.id) { index, item in
                NavigationLink(value: item) {
                    GalleryTile(item: item, library: model.library)
                        .matchedTransitionSource(id: item.id, in: zoom)
                }
                .buttonStyle(.plain)
                .onAppear { model.appeared(index) }
                .onDisappear { model.disappeared(index) }
            }
        }
        .animation(Motion.numericAppear.unlessReduceMotion(reduceMotion), value: model.items)
    }

    private var limitedBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "photo.badge.checkmark")
                .font(.title3)
                .foregroundStyle(Palette.uiInk2.color)
                .accessibilityHidden(true)
            Text("Видны только выбранные фото.")
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk.color)
            Spacer()
            Button("Выбрать ещё") {
                Task { await model.manageLimitedSelection() }
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(Palette.uiInk.color)
        }
        .contentCard(padding: 14)
        .padding(.horizontal, 16)
    }

    private func permissionCard(title: String, text: String, button: String, action: @escaping () -> Void) -> some View
    {
        VStack(spacing: 14) {
            Image(systemName: "photo.on.rectangle.angled")
                .font(.system(size: 44, weight: .semibold))
                .foregroundStyle(Palette.uiInk3.color)
                .accessibilityHidden(true)
            Text(title)
                .font(.title3.bold())
                .fontDesign(.rounded)
                .foregroundStyle(Palette.uiInk.color)
            Text(text)
                .font(.body)
                .foregroundStyle(Palette.uiInk2.color)
                .multilineTextAlignment(.center)
            Button(button, action: action)
                .buttonStyle(.neutral)
        }
        .contentCard(padding: 24)
        .padding(.horizontal, 16)
    }
}

extension GalleryFilter {
    var emptySymbol: String {
        switch self {
        case .all: "photo"
        case .geotagged: "location.slash"
        case .videos: "video.slash"
        }
    }

    var emptyTitle: String {
        switch self {
        case .all: "Пока пусто"
        case .geotagged: "Нет фото с геотегом"
        case .videos: "Видео нет"
        }
    }

    var emptyText: String {
        switch self {
        case .all: "Сними что-нибудь на забеге — фото появятся здесь."
        case .geotagged: "Включи геопозицию для Камеры — фото запомнят, где сняты."
        case .videos: "Собери видео-повтор забега и сохрани его в «Фото»."
        }
    }
}

/// Плитка сетки: миниатюра из кэша, у видео — длительность, у снятых с геотегом — значок.
private struct GalleryTile: View {
    let item: GalleryItem
    let library: any PhotoLibraryProviding
    @State private var image: UIImage?

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                        .transition(.opacity)
                } else {
                    Palette.uiCell2.color
                }
            }
            .clipped()
            .overlay(alignment: .bottomLeading) { badges }
            .contentShape(.rect)
            .task(id: item.id) {
                let loaded = await library.thumbnail(for: item, size: GalleryModel.thumbnailSize)
                withAnimation(.easeOut(duration: 0.2)) { image = loaded }
            }
            .accessibilityElement()
            .accessibilityLabel(item.accessibilityLabel)
            .accessibilityAddTraits(.isButton)
    }

    private var badges: some View {
        HStack(spacing: 4) {
            if item.isVideo {
                Image(systemName: "play.fill")
                Text(Duration.seconds(item.duration).formatted(.time(pattern: .minuteSecond)))
                    .monospacedDigit()
            }
            if item.hasLocation {
                Image(systemName: "location.fill")
            }
        }
        .font(.caption2.weight(.semibold))
        .foregroundStyle(.white)
        .shadow(color: .black.opacity(0.6), radius: 2)
        .padding(6)
    }
}

extension GalleryItem {
    var accessibilityLabel: String {
        var parts = [isVideo ? "Видео" : "Фото"]
        if let createdAt {
            parts.append(createdAt.formatted(.dateTime.day().month(.wide).year().locale(Locale(identifier: "ru_RU"))))
        }
        if hasLocation { parts.append("с геотегом") }
        return parts.joined(separator: ", ")
    }
}

/// Просмотр: фото крупно, видео — плеером; где и когда снято.
struct GalleryDetailView: View {
    let item: GalleryItem
    let library: any PhotoLibraryProviding
    @State private var image: UIImage?
    @State private var player: AVPlayer?

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Color.black
                if let player {
                    VideoPlayer(player: player)
                } else if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                } else {
                    ProgressView()
                        .tint(.white)
                }
            }
            .ignoresSafeArea(edges: .top)
            info
        }
        .background(Palette.uiBackground.color)
        .navigationTitle(item.isVideo ? "Видео" : "Фото")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            image = await library.thumbnail(for: item, size: CGSize(width: 1_200, height: 1_200))
            if item.isVideo, let box = await library.playerItem(for: item) {
                player = AVPlayer(playerItem: box.item)
            }
        }
        .onDisappear { player?.pause() }
    }

    private var info: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let createdAt = item.createdAt {
                Label(
                    createdAt.formatted(
                        .dateTime.day().month(.wide).year().hour().minute().locale(Locale(identifier: "ru_RU"))),
                    systemImage: "calendar")
            }
            if let latitude = item.latitude, let longitude = item.longitude {
                Label(
                    NumberText.decimal(latitude, fractionDigits: 5) + "° с. ш., "
                        + NumberText.decimal(longitude, fractionDigits: 5) + "° в. д.",
                    systemImage: "location")
            } else {
                Label("Без геотега", systemImage: "location.slash")
            }
        }
        .font(.subheadline)
        .foregroundStyle(Palette.uiInk2.color)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(20)
    }
}
