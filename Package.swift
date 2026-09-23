// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "ScreenPlus",
    platforms: [.macOS(.v15)],
    targets: [
        .executableTarget(
            name: "ScreenPlus",
            path: "Sources/ScreenPlus"
        )
    ],
    swiftLanguageModes: [.v5]
)
