// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "AiAssistantMacHelper",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .executable(name: "aiassistant-mac-helper", targets: ["MacHelper"])
    ],
    targets: [
        .executableTarget(
            name: "MacHelper",
            path: "Sources/MacHelper"
        )
    ]
)
