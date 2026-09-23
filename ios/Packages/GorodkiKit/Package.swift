// swift-tools-version: 6.2
// Общий код приложения и расширения: дизайн-система и платформенные сервисы iOS.
// Чистая игровая логика живёт отдельно — в пакете GameCore.

import PackageDescription

let package = Package(
    name: "GorodkiKit",
    platforms: [.iOS(.v26)],
    products: [
        .library(name: "DesignSystem", targets: ["DesignSystem"]),
        .library(name: "Platform", targets: ["Platform"]),
    ],
    targets: [
        // Цвета, шрифты и общие элементы интерфейса.
        .target(name: "DesignSystem"),
        // App Group, Live Activity и прочие возможности iOS, которые нужны и приложению, и расширению.
        .target(name: "Platform"),
    ]
)
