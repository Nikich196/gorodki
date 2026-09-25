// swift-tools-version: 6.2
// Сквозной прогон (docs/guides/e2e.md): забег телефона — RunTracker, RunSession, SyncEngine — против настоящего сервера
// и настоящей базы. Только тесты, в приложение не входит; в обычные прогоны ios-core не попадает (там пакеты
// перечислены явно). Без GORODKI_E2E_SERVER набор пропускается; сервер, игроков и токены готовит .github/workflows/e2e.yml.

import PackageDescription

let package = Package(
    name: "GorodkiE2E",
    platforms: [.iOS(.v26), .macOS(.v26)],
    dependencies: [
        .package(path: "../GameCore"),
        .package(path: "../GorodkiAPI"),
        .package(path: "../GorodkiSync"),
        .package(path: "../GorodkiNetwork"),
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.0.0"),
    ],
    targets: [
        .testTarget(
            name: "E2ETests",
            dependencies: [
                .product(name: "GameCore", package: "GameCore"),
                .product(name: "GorodkiAPI", package: "GorodkiAPI"),
                .product(name: "Sync", package: "GorodkiSync"),
                .product(name: "Networking", package: "GorodkiNetwork"),
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        )
    ]
)
