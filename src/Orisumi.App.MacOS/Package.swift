// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "OrisumiApp",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "OrisumiApp", targets: ["OrisumiApp"])],
    targets: [
        .executableTarget(name: "OrisumiApp"),
        .testTarget(name: "OrisumiAppTests", dependencies: ["OrisumiApp"], resources: [.copy("Fixtures")]),
    ]
)
