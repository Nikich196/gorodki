// swift-tools-version: 6.2
// Постоянное хранилище приложения на GRDB (PLAN.md, D5: «GRDB 7, SQLite, WAL, в Application Support»). Первым здесь —
// очередь синхронизации: забеги, куски и заявки переживают выгрузку приложения и перезапуск телефона.
// Без UIKit и SwiftUI — тестируется на Linux (нужен libsqlite3-dev, в CI ставится шагом ios-core).

import PackageDescription

let package = Package(
    name: "GorodkiPersistence",
    platforms: [.iOS(.v26), .macOS(.v26)],
    products: [
        .library(name: "Persistence", targets: ["Persistence"])
    ],
    dependencies: [
        .package(path: "../GameCore"),
        .package(path: "../GorodkiSync"),
        .package(url: "https://github.com/groue/GRDB.swift.git", from: "7.11.0"),
    ],
    targets: [
        .target(
            name: "Persistence",
            dependencies: [
                .product(name: "Sync", package: "GorodkiSync"),
                .product(name: "GRDB", package: "GRDB.swift"),
            ]
        ),
        .testTarget(
            name: "PersistenceTests",
            dependencies: [
                "Persistence",
                .product(name: "Sync", package: "GorodkiSync"),
                .product(name: "GameCore", package: "GameCore"),
                .product(name: "GRDB", package: "GRDB.swift"),
            ]
        ),
    ]
)
