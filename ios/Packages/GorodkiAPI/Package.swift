// swift-tools-version: 6.2
// Клиент API сервера «Городков», сгенерированный из contracts/openapi.v1.json (спайк S6, docs/adr/0004-openapi-swift.md).
// Код клиента не хранится в репозитории: его пишет плагин swift-openapi-generator при сборке.

import PackageDescription

let package = Package(
    name: "GorodkiAPI",
    platforms: [.iOS(.v26), .macOS(.v26)],
    products: [
        .library(name: "GorodkiAPI", targets: ["GorodkiAPI"])
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-openapi-generator", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.0.0"),
    ],
    targets: [
        .target(
            name: "GorodkiAPI",
            dependencies: [.product(name: "OpenAPIRuntime", package: "swift-openapi-runtime")],
            plugins: [.plugin(name: "OpenAPIGenerator", package: "swift-openapi-generator")]
        ),
        .testTarget(
            name: "GorodkiAPITests",
            dependencies: [
                "GorodkiAPI",
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        ),
    ]
)
