// swift-tools-version: 6.2
// Чистая игровая логика «Городков» без UIKit, SwiftUI и CoreLocation.
// Собирается и тестируется где угодно, в том числе на Linux и в WSL: `swift test`.

import PackageDescription

let package = Package(
    name: "GameCore",
    platforms: [.iOS(.v26), .macOS(.v26)],
    products: [
        .library(name: "GameCore", targets: ["GameCore"])
    ],
    targets: [
        .target(name: "GameCore"),
        .testTarget(name: "GameCoreTests", dependencies: ["GameCore"]),
    ]
)
