// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "CombineColors",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "AstroCore", targets: ["AstroCore"]),
        .executable(name: "CombineColors", targets: ["CombineColors"]),
        .executable(name: "combinecolors-cli", targets: ["combinecolors-cli"]),
    ],
    targets: [
        .target(
            name: "AstroCore",
            path: "Sources/AstroCore",
            swiftSettings: [.unsafeFlags(["-Ounchecked"], .when(configuration: .release))]
        ),
        .executableTarget(
            name: "CombineColors",
            dependencies: ["AstroCore"],
            path: "Sources/CombineColors",
            resources: [.copy("Resources")]
        ),
        .executableTarget(
            name: "combinecolors-cli",
            dependencies: ["AstroCore"],
            path: "Sources/combinecolors-cli"
        ),
        .testTarget(
            name: "AstroCoreTests",
            dependencies: ["AstroCore"],
            path: "Tests/AstroCoreTests"
        ),
    ],
    swiftLanguageVersions: [.v5]
)
