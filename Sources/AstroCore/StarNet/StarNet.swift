import Foundation

/// StarNet2 (https://starnetastro.com, by Mikita Misiura) is a separate, free command-line program
/// that removes stars. CombineColors never bundles it: the user downloads it from the official
/// site (the app can do that for them) and accepts its license; the app then runs the executable
/// as a separate process, which is the integration its license allows.
public enum StarNet {
    public static let website = URL(string: "https://starnetastro.com/cli-tools/starnet/")!
    /// Official machine-readable feed of the latest CLI releases (direct archive links + SHA-256).
    public static let latestFeed = URL(string: "https://starnetastro.com/cli-tools/latest.json")!
    /// Name of the executable in every StarNet2 CLI package.
    public static let executableName = "starnet2"
    /// StarNet2 refuses smaller images (256 with `--upsample`).
    public static let minimumSize = 512

    /// Platform key of the feed for this Mac (Apple Silicon even when running under Rosetta).
    public static var platformKey: String {
        var v: Int32 = 0
        var size = MemoryLayout<Int32>.size
        let arm = sysctlbyname("hw.optional.arm64", &v, &size, nil, 0) == 0 && v == 1
        return arm ? "macos-arm64" : "macos-x64"
    }

    /// Places where a StarNet2 installed by its own .pkg installer or by Homebrew lives.
    public static let standardLocations: [URL] = [
        URL(fileURLWithPath: "/usr/local/bin/starnet2"),
        URL(fileURLWithPath: "/opt/homebrew/bin/starnet2"),
    ]

    /// License files to show for an executable: the one shipped next to it (portable archive) or
    /// the one the .pkg installer puts under /usr/local/share/doc.
    public static func licenseFile(for executable: URL) -> URL? {
        let dir = executable.resolvingSymlinksInPath().deletingLastPathComponent()
        let candidates = [
            dir.appendingPathComponent("LICENSE.txt"),
            dir.deletingLastPathComponent().appendingPathComponent("share/doc/starnet2/LICENSE.txt"),
            URL(fileURLWithPath: "/usr/local/share/doc/starnet2/LICENSE.txt"),
        ]
        return candidates.first { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// Finds the starnet2 executable inside a folder (an unpacked archive), a few levels deep.
    public static func findExecutable(in folder: URL, depth: Int = 3) -> URL? {
        let fm = FileManager.default
        let direct = folder.appendingPathComponent(executableName)
        if fm.isExecutableFile(atPath: direct.path), !isDirectory(direct) { return direct }
        guard depth > 0, let items = try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else { return nil }
        for item in items.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) where isDirectory(item) && item.pathExtension != "mlpackage" {
            if let found = findExecutable(in: item, depth: depth - 1) { return found }
        }
        return nil
    }

    static func isDirectory(_ url: URL) -> Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &dir) && dir.boolValue
    }

    /// "2.6.10" > "2.6.2".
    public static func isVersion(_ a: String, newerThan b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }
}

/// What `starnet2 --machine-info` reports.
public struct StarNetInfo: Equatable, Sendable {
    public var version: String
    public var build: String
    /// Inference backend ("CoreML" on Apple Silicon, "ONNX Runtime" elsewhere).
    public var backend: String

    public init(version: String, build: String, backend: String) { self.version = version; self.build = build; self.backend = backend }

    /// Parses the `--machine-info` JSON; nil when it is not StarNet2's.
    public static func parse(_ data: Data) -> StarNetInfo? {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              (obj["product"] as? String)?.lowercased() == "starnet2",
              let version = obj["version"] as? String else { return nil }
        let backend = (obj["backend"] as? [String: Any])?["display_name"] as? String ?? ""
        return StarNetInfo(version: version, build: obj["build"] as? String ?? "", backend: backend)
    }
}

/// One release package from the official feed.
public struct StarNetRelease: Equatable, Sendable {
    public var version: String
    public var url: URL
    public var sha256: String?
    public var sizeBytes: Int?
    public var fileName: String { url.lastPathComponent }

    public init(version: String, url: URL, sha256: String?, sizeBytes: Int?) {
        self.version = version; self.url = url; self.sha256 = sha256; self.sizeBytes = sizeBytes
    }

    /// The portable archive for a platform key, from the `latest.json` feed.
    public static func parseFeed(_ data: Data, platform: String = StarNet.platformKey) -> StarNetRelease? {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tools = obj["tools"] as? [String: Any],
              let tool = tools["starnet2"] as? [String: Any],
              let latest = tool["latest"] as? [String: Any],
              let plat = latest[platform] as? [String: Any],
              let version = plat["version"] as? String,
              let packages = plat["packages"] as? [String: Any],
              let archive = packages["archive"] as? [String: Any],
              let s = archive["url"] as? String, let url = URL(string: s), url.scheme == "https" else { return nil }
        return StarNetRelease(version: version, url: url, sha256: archive["sha256"] as? String, sizeBytes: archive["size_bytes"] as? Int)
    }

    /// Used when the feed cannot be read (release of 2026-09-19).
    public static func fallback(platform: String = StarNet.platformKey) -> StarNetRelease {
        if platform == "macos-arm64" {
            return StarNetRelease(version: "2.6.2", url: URL(string: "https://download.starnetastro.com/starnet2_macos-arm64_2.6.2-0242_COREML_arm64_cli.zip")!,
                                  sha256: "dfeb471e983a9de1233b439a8c56d3e373ed26e37c85aababe737187d164d20f", sizeBytes: 127_977_753)
        }
        return StarNetRelease(version: "2.6.2", url: URL(string: "https://download.starnetastro.com/starnet2_macos-x64_2.6.2-0242_ORT_x64_cli.zip")!,
                              sha256: "e0af667ae9d1a0e6d869d5fdef1738aa2c9077dc62bcbada706a88d9cc6a524a", sizeBytes: nil)
    }
}

public enum StarNetError: Error, LocalizedError, Equatable {
    case notStarNet2(String)
    case imageTooSmall(Int, Int)
    case unsupportedPlanes(Int)
    case failed(status: Int32, message: String)
    case missingOutput(String)
    case cancelled

    public var errorDescription: String? {
        switch self {
        case .notStarNet2(let p): return "\(p) is not StarNet2 version 2.6 or newer."
        case .imageTooSmall(let w, let h): return "StarNet2 needs images of at least \(StarNet.minimumSize) × \(StarNet.minimumSize) pixels (this one is \(w) × \(h))."
        case .unsupportedPlanes(let n): return "StarNet2 takes grey or RGB images (this one has \(n) planes)."
        case .failed(let status, let message): return "StarNet2 stopped with an error (code \(status))." + (message.isEmpty ? "" : "\n" + message)
        case .missingOutput(let f): return "StarNet2 did not write \(f)."
        case .cancelled: return "Star removal was stopped."
        }
    }
}

/// Stops a running StarNet2 process from another thread.
public final class StarNetCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var cancelled = false

    public init() {}

    public var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }

    public func cancel() {
        lock.lock()
        cancelled = true
        let p = process
        lock.unlock()
        if let p, p.isRunning { p.terminate() }
    }

    /// Registers the running process; false when the job was cancelled already.
    func attach(_ p: Process) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if cancelled { return false }
        process = p
        return true
    }

    func detach() { lock.lock(); process = nil; lock.unlock() }
}

/// The arguments of one StarNet2 run.
public struct StarNetCommand: Equatable {
    public var input: URL
    public var output: URL
    /// Difference star layer (`--mask`): original − starless, the natural layer for linear data.
    public var mask: URL? = nil
    /// Unscreen star layer (`--unscreen`): Screen it on the starless image to get the original back.
    public var unscreen: URL? = nil
    /// `--linear`: StarNet2 stretches the data itself (per-channel MTF) and undoes the stretch.
    public var linear = false
    public var shadowsClipping: Float? = nil
    public var targetBackground: Float? = nil
    public var upsample = false

    public init(input: URL, output: URL) { self.input = input; self.output = output }

    public var arguments: [String] {
        var a = ["--machine-progress", "-i", input.path, "-o", output.path]
        if let m = mask { a += ["--mask", m.path] }
        if let u = unscreen { a += ["--unscreen", u.path] }
        if linear {
            a.append("--linear")
            // StarNet2 wants a negative clipping factor and a target strictly inside (0, 1).
            if let s = shadowsClipping { a += ["--shadows-clipping", String(format: "%.3f", min(s, -0.01))] }
            if let t = targetBackground { a += ["--target-background", String(format: "%.3f", min(max(t, 0.01), 0.99))] }
        }
        if upsample { a.append("--upsample") }
        return a
    }
}

/// A StarNet2 executable.
public struct StarNetTool: Sendable, Equatable {
    public let executable: URL

    public init(executable: URL) { self.executable = executable }

    /// Asks the executable for its version (`--machine-info`); throws when it is not StarNet2 2.6+.
    public func info(timeout: TimeInterval = 30) throws -> StarNetInfo {
        let p = Process()
        p.executableURL = executable
        p.arguments = ["--machine-info"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { throw StarNetError.notStarNet2(executable.lastPathComponent) }
        var data = Data()
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async { data = out.fileHandleForReading.readDataToEndOfFile(); group.leave() }
        if group.wait(timeout: .now() + timeout) == .timedOut { p.terminate() }
        p.waitUntilExit()
        _ = group.wait(timeout: .now() + 2)
        guard p.terminationStatus == 0, let info = StarNetInfo.parse(data) else { throw StarNetError.notStarNet2(executable.lastPathComponent) }
        return info
    }

    /// Runs one command, reporting progress in [0, 1] from StarNet2's JSON-lines progress events.
    public func run(_ command: StarNetCommand, progress: ((Double) -> Void)? = nil, cancellation: StarNetCancellation? = nil) throws {
        let p = Process()
        p.executableURL = executable
        p.arguments = command.arguments
        p.currentDirectoryURL = command.output.deletingLastPathComponent()
        p.standardOutput = FileHandle.nullDevice
        let err = Pipe()
        p.standardError = err
        if let c = cancellation, !c.attach(p) { throw StarNetError.cancelled }
        defer { cancellation?.detach() }
        try p.run()
        var messages: [String] = []
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async {
            var buffer = Data()
            let handle = err.fileHandleForReading
            func consume(_ line: Data) {
                guard !line.isEmpty else { return }
                if let ev = StarNetEvent.parse(line) {
                    switch ev {
                    case .progress(let f): progress?(f)
                    case .message(let m): messages.append(m)
                    case .other: break
                    }
                } else if let s = String(data: line, encoding: .utf8) {
                    messages.append(s.trimmingCharacters(in: .whitespacesAndNewlines))
                }
            }
            while true {
                let chunk = handle.availableData
                if chunk.isEmpty { break }
                buffer.append(chunk)
                while let nl = buffer.firstIndex(of: 0x0A) {
                    consume(buffer[buffer.startIndex..<nl])
                    buffer.removeSubrange(buffer.startIndex...nl)
                }
            }
            consume(buffer)
            group.leave()
        }
        p.waitUntilExit()
        group.wait()
        if cancellation?.isCancelled == true { throw StarNetError.cancelled }
        guard p.terminationStatus == 0 else {
            throw StarNetError.failed(status: p.terminationStatus, message: messages.suffix(6).joined(separator: "\n"))
        }
    }
}

/// One line of StarNet2's machine output on stderr.
enum StarNetEvent: Equatable {
    case progress(Double)
    /// A warning or an error worth showing when the run fails.
    case message(String)
    case other

    static func parse(_ line: Data) -> StarNetEvent? {
        guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any], let schema = obj["schema"] as? String else { return nil }
        if schema.hasPrefix("starnetastro.cli.progress") {
            let event = obj["event"] as? String
            if event == "error" { return .message(obj["message"] as? String ?? "error") }
            if let pct = obj["percent"] as? Double { return .progress(min(max(pct / 100, 0), 1)) }
            return .other
        }
        if schema.hasPrefix("starnetastro.cli.diagnostic"), let sev = obj["severity"] as? String, sev != "info" {
            return .message(obj["message"] as? String ?? sev)
        }
        return .other
    }
}
