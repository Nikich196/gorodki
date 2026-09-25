// swift-tools-version: 6.2
// Разбор записанного забега, половина телефона (docs/guides/calibration-replay.md): точки из «Моих данных» проходят
// судью и детектор петли GameCore с заданными числами, найденные петли уходят в JSON для половины сервера
// (backend/tools/Gorodki.Calibration). Инструмент разработчика, в приложение не входит. Собирается на Linux и в WSL.

import PackageDescription

let package = Package(
    name: "LoopReplay",
    platforms: [.macOS(.v26)],
    dependencies: [
        .package(path: "../../Packages/GameCore")
    ],
    targets: [
        .target(name: "LoopReplayKit", dependencies: [.product(name: "GameCore", package: "GameCore")]),
        .executableTarget(name: "LoopReplay", dependencies: ["LoopReplayKit"]),
        .testTarget(
            name: "LoopReplayKitTests",
            dependencies: ["LoopReplayKit", .product(name: "GameCore", package: "GameCore")]),
    ]
)
