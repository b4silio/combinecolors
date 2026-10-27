import Foundation
import AstroCore

/// A group of channel files that were loaded together, remembered as one "recent" entry.
struct RecentSet: Codable, Identifiable, Equatable {
    struct Entry: Codable, Equatable {
        var kind: ChannelKind
        var path: String
    }
    var id: UUID = UUID()
    var name: String
    var entries: [Entry]
    var date: Date

    var paths: Set<String> { Set(entries.map(\.path)) }
    var folder: String { (entries.first?.path as NSString?)?.deletingLastPathComponent ?? "" }
    var summary: String { entries.map(\.kind.shortName).joined(separator: " · ") }

    /// Builds a readable name: the longest common file-name prefix, else the folder name.
    static func makeName(entries: [Entry]) -> String {
        let names = entries.map { (($0.path as NSString).lastPathComponent as NSString).deletingPathExtension }
        guard var prefix = names.first else { return "Set" }
        for n in names.dropFirst() {
            while !n.hasPrefix(prefix) && !prefix.isEmpty { prefix.removeLast() }
        }
        prefix = prefix.trimmingCharacters(in: CharacterSet(charactersIn: "-_ ."))
        if prefix.count >= 3 { return prefix }
        let folder = ((entries.first?.path ?? "") as NSString).deletingLastPathComponent
        return (folder as NSString).lastPathComponent.isEmpty ? "Set" : (folder as NSString).lastPathComponent
    }
}

/// Persistent most-recently-used list of channel sets (UserDefaults backed).
final class RecentSetsStore {
    static let key = "recentChannelSets"
    static let limit = 12
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    func load() -> [RecentSet] {
        guard let data = defaults.data(forKey: RecentSetsStore.key),
              let sets = try? JSONDecoder().decode([RecentSet].self, from: data) else { return [] }
        return sets
    }

    func save(_ sets: [RecentSet]) {
        defaults.set((try? JSONEncoder().encode(sets)) ?? Data(), forKey: RecentSetsStore.key)
    }

    /// Inserts (or moves to the front) a set with these entries. Same file paths = same set.
    @discardableResult
    func record(entries: [RecentSet.Entry]) -> [RecentSet] {
        guard !entries.isEmpty else { return load() }
        let sorted = entries.sorted { $0.kind.rawValue < $1.kind.rawValue }
        var sets = load().filter { $0.paths != Set(sorted.map(\.path)) }
        sets.insert(RecentSet(name: RecentSet.makeName(entries: sorted), entries: sorted, date: Date()), at: 0)
        if sets.count > RecentSetsStore.limit { sets.removeLast(sets.count - RecentSetsStore.limit) }
        save(sets)
        return sets
    }

    func remove(id: UUID) -> [RecentSet] {
        let sets = load().filter { $0.id != id }
        save(sets)
        return sets
    }

    func clear() { save([]) }
}
