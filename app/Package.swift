// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "BHServe",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "BHServe",
            path: "Sources/BHServe"
        )
    ]
)
