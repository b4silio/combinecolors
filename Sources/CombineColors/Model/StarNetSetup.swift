import Foundation
import CryptoKit
import AstroCore

/// Finds, downloads and remembers the StarNet2 command-line program, and keeps track of the
/// user's acceptance of its license (StarNet2's terms require integrations to show the license
/// and get an explicit "I accept" before using it; downloads must come from the official site).
@MainActor
@Observable
final class StarNetSetup {
    enum Phase: Equatable {
        case checking
        /// Nothing found: offer the download.
        case missing
        case downloading(received: Int64, total: Int64)
        case installing
        /// Installed; the license has not been accepted yet.
        case needsLicense
        case ready
        case failed(String)
    }

    private(set) var phase: Phase = .checking
    private(set) var tool: StarNetTool? = nil
    private(set) var info: StarNetInfo? = nil
    /// The license text to accept (nil: no license file next to the program — the website's applies).
    private(set) var licenseText: String? = nil
    /// Newest release on the official feed (after `checkForUpdate`).
    private(set) var latest: StarNetRelease? = nil

    var updateAvailable: Bool {
        guard let l = latest, let i = info else { return false }
        return StarNet.isVersion(l.version, newerThan: i.version)
    }

    /// Folder the app installs StarNet2 into (no administrator password needed).
    let installRoot: URL
    private let defaults: UserDefaults
    private var downloadTask: URLSessionDownloadTask? = nil
    private var progressObservation: NSKeyValueObservation? = nil

    static let pathKey = "starnetExecutable"
    static let licenseKey = "starnetLicenseAccepted"

    init(defaults: UserDefaults, installRoot: URL? = nil) {
        self.defaults = defaults
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? URL(fileURLWithPath: NSTemporaryDirectory())
        self.installRoot = installRoot ?? support.appendingPathComponent("CombineColors/StarNet2", isDirectory: true)
    }

    // MARK: Finding the program

    /// Looks for StarNet2: the remembered path, the app's own install, then the standard places.
    func refresh() {
        phase = .checking
        var candidates: [URL] = []
        if let p = defaults.string(forKey: Self.pathKey) { candidates.append(URL(fileURLWithPath: p)) }
        if let own = StarNet.findExecutable(in: installRoot) { candidates.append(own) }
        candidates += StarNet.standardLocations
        let list = candidates.filter { FileManager.default.isExecutableFile(atPath: $0.path) }
        Task.detached(priority: .userInitiated) {
            for url in list {
                let t = StarNetTool(executable: url)
                if let info = try? t.info() {
                    await MainActor.run { self.adopt(t, info: info) }
                    return
                }
            }
            await MainActor.run {
                self.tool = nil; self.info = nil
                self.phase = .missing
            }
        }
    }

    /// Uses a file the user picked: the starnet2 program itself, the folder it is in, or the
    /// downloaded .zip archive (installed like a download).
    func choose(_ url: URL) {
        if url.pathExtension.lowercased() == "zip" { install(archive: url, expectedSHA256: nil, version: nil); return }
        let exe = StarNet.isDirectoryURL(url) ? StarNet.findExecutable(in: url) : url
        guard let exe else { phase = .failed("No program called “starnet2” was found in \(url.lastPathComponent)."); return }
        phase = .checking
        Task.detached(priority: .userInitiated) {
            let t = StarNetTool(executable: exe)
            do {
                let info = try t.info()
                await MainActor.run { self.adopt(t, info: info) }
            } catch {
                await MainActor.run { self.phase = .failed(error.localizedDescription + " Please pick the file called “starnet2” from the StarNet2 command-line package.") }
            }
        }
    }

    private func adopt(_ t: StarNetTool, info: StarNetInfo) {
        tool = t
        self.info = info
        defaults.set(t.executable.path, forKey: Self.pathKey)
        licenseText = StarNet.licenseFile(for: t.executable).flatMap { try? String(contentsOf: $0, encoding: .utf8) }
        phase = licenseAccepted ? .ready : .needsLicense
    }

    // MARK: License

    /// Accepted when the stored fingerprint matches the current license text (revised terms ask again).
    var licenseAccepted: Bool { defaults.string(forKey: Self.licenseKey) == licenseFingerprint }

    private var licenseFingerprint: String {
        guard let text = licenseText else { return "website" }
        return SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    func acceptLicense() {
        defaults.set(licenseFingerprint, forKey: Self.licenseKey)
        if tool != nil { phase = .ready }
    }

    // MARK: Download

    /// Downloads the portable package for this Mac from the official site, checks it and unpacks
    /// it into Application Support.
    func download() {
        phase = .downloading(received: 0, total: 0)
        Task {
            let release = await Self.fetchLatest() ?? StarNetRelease.fallback()
            latest = release
            startDownload(release)
        }
    }

    private func startDownload(_ release: StarNetRelease) {
        guard case .downloading = phase else { return }   // cancelled while the feed was read
        phase = .downloading(received: 0, total: Int64(release.sizeBytes ?? 0))
        let task = URLSession.shared.downloadTask(with: release.url) { [weak self] tmp, response, error in
            // The temporary file disappears when this handler returns: move it first.
            var kept: URL? = nil
            if let tmp, error == nil, ((response as? HTTPURLResponse)?.statusCode ?? 200) == 200 {
                let dest = FileManager.default.temporaryDirectory.appendingPathComponent("starnet2-\(UUID().uuidString).zip")
                if (try? FileManager.default.moveItem(at: tmp, to: dest)) != nil { kept = dest }
            }
            let message: String? = {
                if let e = error as? URLError, e.code == .cancelled { return nil }
                if let e = error { return e.localizedDescription }
                if let code = (response as? HTTPURLResponse)?.statusCode, code != 200 { return "The website answered with error \(code)." }
                return kept == nil ? "The downloaded file could not be kept." : nil
            }()
            let cancelled = (error as? URLError)?.code == .cancelled
            let archive = kept
            Task { @MainActor in
                guard let self else { return }
                self.progressObservation = nil
                self.downloadTask = nil
                if cancelled { self.refresh(); return }
                if let archive { self.install(archive: archive, expectedSHA256: release.sha256, version: release.version, deleteArchive: true) }
                else { self.phase = .failed("The download did not work: \(message ?? "unknown error"). Check the internet connection and try again, or download StarNet2 from its website yourself.") }
            }
        }
        progressObservation = task.progress.observe(\.fractionCompleted) { [weak self] p, _ in
            let received = p.completedUnitCount, total = p.totalUnitCount
            Task { @MainActor in
                guard let self, case .downloading = self.phase else { return }
                self.phase = .downloading(received: received, total: total > 0 ? total : Int64(release.sizeBytes ?? 0))
            }
        }
        downloadTask = task
        task.resume()
    }

    func cancelDownload() {
        if let t = downloadTask { t.cancel() } else { refresh() }
    }

    /// Reads the official feed (nil when offline).
    static func fetchLatest() async -> StarNetRelease? {
        var req = URLRequest(url: StarNet.latestFeed, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        req.setValue("CombineColors", forHTTPHeaderField: "User-Agent")
        guard let (data, resp) = try? await URLSession.shared.data(for: req), (resp as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return StarNetRelease.parseFeed(data)
    }

    func checkForUpdate() {
        Task { if let r = await Self.fetchLatest() { latest = r } }
    }

    /// Unpacks an archive into `installRoot/<version>`, replacing an earlier install.
    func install(archive: URL, expectedSHA256: String?, version: String?, deleteArchive: Bool = false) {
        phase = .installing
        let root = installRoot
        Task.detached(priority: .userInitiated) {
            defer { if deleteArchive { try? FileManager.default.removeItem(at: archive) } }
            do {
                if let expected = expectedSHA256?.lowercased(), try Self.sha256(of: archive) != expected {
                    throw InstallError("The downloaded file is damaged (its checksum does not match). Please try again.")
                }
                let fm = FileManager.default
                try fm.createDirectory(at: root, withIntermediateDirectories: true)
                let staging = root.appendingPathComponent(".unpacking-\(UUID().uuidString)", isDirectory: true)
                try fm.createDirectory(at: staging, withIntermediateDirectories: true)
                defer { try? fm.removeItem(at: staging) }
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
                p.arguments = ["-x", "-k", archive.path, staging.path]
                p.standardOutput = FileHandle.nullDevice
                p.standardError = FileHandle.nullDevice
                try p.run()
                p.waitUntilExit()
                guard p.terminationStatus == 0 else { throw InstallError("The archive could not be unpacked.") }
                guard let exe = StarNet.findExecutable(in: staging) else { throw InstallError("This archive does not contain the starnet2 program.") }
                let info = try StarNetTool(executable: exe).info()
                // Keep the package folder together (program, model, libraries, license) under its version.
                let packageDir = exe.deletingLastPathComponent()
                let dest = root.appendingPathComponent(version ?? info.version, isDirectory: true)
                for old in (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? [] where !old.lastPathComponent.hasPrefix(".") {
                    try? fm.removeItem(at: old)
                }
                try fm.moveItem(at: packageDir, to: dest)
                let installed = dest.appendingPathComponent(StarNet.executableName)
                await MainActor.run { self.adopt(StarNetTool(executable: installed), info: info) }
            } catch {
                await MainActor.run { self.phase = .failed(error.localizedDescription) }
            }
        }
    }

    nonisolated static func sha256(of url: URL) throws -> String {
        let h = try FileHandle(forReadingFrom: url)
        defer { try? h.close() }
        var hasher = SHA256()
        while let chunk = try h.read(upToCount: 4 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Forgets the remembered program (the app's own install is removed too).
    func forget() {
        defaults.removeObject(forKey: Self.pathKey)
        if let t = tool, t.executable.path.hasPrefix(installRoot.path) { try? FileManager.default.removeItem(at: installRoot) }
        tool = nil; info = nil; licenseText = nil
        phase = .missing
    }

    struct InstallError: LocalizedError {
        let message: String
        init(_ m: String) { message = m }
        var errorDescription: String? { message }
    }
}

extension StarNet {
    static func isDirectoryURL(_ url: URL) -> Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &dir) && dir.boolValue
    }
}
