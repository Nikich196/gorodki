// swift-tools-version: 6.2
// Сетевой слой приложения (PLAN.md, §7.2: Networking, `TokenStore`): клиент API поверх URLSession, токены входа
// и их обновление при 401 (docs/architecture/auth.md). Без UIKit и SwiftUI — тестируется на Linux; Keychain есть только
// на платформах Apple и спрятан за протоколом `TokenStorage`. Как это собрано в приложении — docs/architecture/ios-app.md.

import PackageDescription

let package = Package(
    name: "GorodkiNetwork",
    platforms: [.iOS(.v26), .macOS(.v26)],
    products: [
        .library(name: "Networking", targets: ["Networking"])
    ],
    dependencies: [
        .package(path: "../GorodkiAPI"),
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-openapi-urlsession", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.0.0"),
    ],
    targets: [
        .target(
            name: "Networking",
            dependencies: [
                .product(name: "GorodkiAPI", package: "GorodkiAPI"),
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "OpenAPIURLSession", package: "swift-openapi-urlsession"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        ),
        .testTarget(
            name: "NetworkingTests",
            dependencies: [
                "Networking",
                .product(name: "GorodkiAPI", package: "GorodkiAPI"),
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        ),
    ]
)
