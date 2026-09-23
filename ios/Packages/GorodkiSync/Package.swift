// swift-tools-version: 6.2
// Офлайн-синхронизация забегов (PLAN.md, §7.2: SyncEngine, outbox): запись кусков и заявок петель на телефоне
// и их доставка на сервер по контракту docs/architecture/runs.md и captures.md. Чистая логика: тестируется на Linux.

import PackageDescription

let package = Package(
    name: "GorodkiSync",
    platforms: [.iOS(.v26), .macOS(.v26)],
    products: [
        .library(name: "Sync", targets: ["Sync"])
    ],
    dependencies: [
        .package(path: "../GameCore"),
        .package(path: "../GorodkiAPI"),
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.0.0"),
    ],
    targets: [
        .target(
            name: "Sync",
            dependencies: [
                .product(name: "GameCore", package: "GameCore"),
                .product(name: "GorodkiAPI", package: "GorodkiAPI"),
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        ),
        .testTarget(
            name: "SyncTests",
            dependencies: [
                "Sync",
                .product(name: "GameCore", package: "GameCore"),
                .product(name: "GorodkiAPI", package: "GorodkiAPI"),
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        ),
    ]
)
