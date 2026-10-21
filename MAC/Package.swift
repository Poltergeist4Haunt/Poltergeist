// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Poltergeist",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "Poltergeist", path: "Sources/Poltergeist")
    ]
)
