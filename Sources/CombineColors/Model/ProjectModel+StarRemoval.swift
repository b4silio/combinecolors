import Foundation
import AstroCore

/// One source file to run StarNet2 on, with the channels it backs (three for a split colour file).
struct StarRemovalItem: Identifiable, Equatable {
    var id: String { source.path }
    let source: URL
    let kinds: [ChannelKind]
    let starless: URL
    let stars: URL
    /// The file is already a `_bg` output.
    let alreadyStarless: Bool
    /// `_bg` / `_stars` files from an earlier run are there and will be replaced.
    let outputsExist: Bool

    var kindsLabel: String { kinds.map(\.shortName).joined(separator: ", ") }
}

struct StarRemovalProgress: Equatable {
    /// Overall fraction done, all files together.
    var fraction: Double
    var label: String
    /// Time left, once it can be estimated.
    var remaining: String
}

extension ProjectModel {
    /// The loaded channels grouped by file, in channel order. Channels without a file (dual-band
    /// splits) are left out.
    func starRemovalItems(folder: URL? = nil) -> [StarRemovalItem] {
        var kindsByPath: [String: [ChannelKind]] = [:]
        var urls: [URL] = []
        for k in ChannelKind.allCases {
            guard let u = sources[k]?.url else { continue }
            if kindsByPath[u.path] == nil { urls.append(u) }
            kindsByPath[u.path, default: []].append(k)
        }
        let fm = FileManager.default
        return urls.map { u in
            let out = StarRemoval.outputURLs(for: u, folder: folder)
            return StarRemovalItem(source: u, kinds: kindsByPath[u.path] ?? [], starless: out.starless, stars: out.stars,
                                   alreadyStarless: StarRemoval.isStarless(u),
                                   outputsExist: fm.fileExists(atPath: out.starless.path) || fm.fileExists(atPath: out.stars.path))
        }
    }

    /// Channels that cannot go through StarNet2 because no file backs them.
    var channelsWithoutFile: [ChannelKind] { ChannelKind.allCases.filter { sources[$0] != nil && sources[$0]?.url == nil } }

    var isRemovingStars: Bool { starRemovalProgress != nil }

    /// Runs StarNet2 on the files one after the other, writes `<name>_bg` and `<name>_stars` for
    /// each and then switches the channels to the `_bg` files (dust spots, gradient models, crop and
    /// painted corrections carry over). Stopping leaves the channels as they were.
    func removeStars(_ items: [StarRemovalItem], options: StarRemovalOptions, folder: URL? = nil) {
        guard let tool = starNet.tool, starNet.licenseAccepted, !items.isEmpty, !isRemovingStars else { return }
        let cancel = StarNetCancellation()
        starRemovalCancellation = cancel
        starRemovalProgress = StarRemovalProgress(fraction: 0, label: "Starting StarNet2…", remaining: "")
        let start = Date()
        Task.detached(priority: .userInitiated) {
            var done: [(StarRemovalItem, StarRemovalFileResult)] = []
            var errors: [String] = []
            for (i, item) in items.enumerated() {
                if cancel.isCancelled { break }
                let label = "\(item.kindsLabel) — \(item.source.lastPathComponent)" + (items.count > 1 ? "  (\(i + 1) of \(items.count))" : "")
                await MainActor.run { self.updateStarRemoval(Double(i) / Double(items.count), label: label, start: start) }
                do {
                    let r = try StarRemoval.processFile(item.source, options: options, tool: tool, folder: folder, progress: { p in
                        let overall = (Double(i) + p) / Double(items.count)
                        Task { @MainActor in self.updateStarRemoval(overall, label: label, start: start) }
                    }, cancellation: cancel)
                    done.append((item, r))
                } catch StarNetError.cancelled {
                    break
                } catch {
                    errors.append("\(item.source.lastPathComponent): \(error.localizedDescription)")
                }
            }
            let finished = done, errs = errors, stopped = cancel.isCancelled
            await MainActor.run { self.finishStarRemoval(finished, errors: errs, stopped: stopped) }
        }
    }

    func stopStarRemoval() { starRemovalCancellation?.cancel() }

    private func updateStarRemoval(_ fraction: Double, label: String, start: Date) {
        guard starRemovalProgress != nil else { return }
        let elapsed = Date().timeIntervalSince(start)
        var remaining = ""
        if fraction > 0.03 {
            let left = elapsed / fraction * (1 - fraction)
            remaining = left < 60 ? "less than a minute left" : "about \(Int((left / 60).rounded())) minute\(left >= 90 ? "s" : "") left"
        }
        starRemovalProgress = StarRemovalProgress(fraction: max(fraction, starRemovalProgress?.fraction ?? 0), label: label, remaining: remaining)
    }

    private func finishStarRemoval(_ done: [(StarRemovalItem, StarRemovalFileResult)], errors: [String], stopped: Bool) {
        starRemovalProgress = nil
        starRemovalCancellation = nil
        if stopped {
            statusText = "Star removal stopped — the channels were not changed."
            return
        }
        if !errors.isEmpty { errorMessage = "StarNet2 could not process:\n" + errors.joined(separator: "\n") }
        guard !done.isEmpty else { return }
        // Painted corrections live next to each image: give the new files a copy.
        for (item, r) in done {
            for k in item.kinds {
                guard let m = correctionMaps[k] else { continue }
                _ = try? m.unoriented(orientation).write(for: r.starless, kind: k)
            }
        }
        let entries = done.flatMap { item, r in item.kinds.map { ($0, r.starless.path) } }
        let count = entries.count
        let stretchedNote = done.contains { $0.1.processing == .alreadyStretched } ? " (already-stretched data processed as they are)" : ""
        isLoading = true
        loadingMessage = "Loading the starless channels…"
        Task.detached(priority: .userInitiated) {
            let (items, errs) = ProjectModel.loadEntries(entries)
            await MainActor.run {
                if !errs.isEmpty { self.errorMessage = errs.joined(separator: "\n") }
                self.statusHint = "stars removed from \(count) channel\(count == 1 ? "" : "s") with StarNet2\(stretchedNote); the _stars files are next to the _bg files"
                if items.isEmpty { self.isLoading = false } else { self.addChannels(items) }
            }
        }
    }
}
