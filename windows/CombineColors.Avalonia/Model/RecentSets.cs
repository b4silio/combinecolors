using System.IO;
using System.Text.Json.Nodes;
using AstroCore;

namespace CombineColors.Model;

/// <summary>A group of channel files that were loaded together, remembered as one "recent" entry.</summary>
public sealed class RecentSet
{
    public sealed record Entry(ChannelKind Kind, string Path);

    public Guid Id = Guid.NewGuid();
    public string Name = "";
    public List<Entry> Entries = new();
    public DateTime Date = DateTime.UtcNow;
    /// <summary>Last settings, crop, rotation and presets used with this set (auto-saved; no channel paths inside).</summary>
    public ProjectDocument? State;

    public HashSet<string> Paths => Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public string Folder => Entries.Count > 0 ? (Path.GetDirectoryName(Entries[0].Path) ?? "") : "";
    public string Summary => string.Join(" · ", Entries.Select(e => e.Kind.ShortName()));

    public bool SamePaths(RecentSet other) => Paths.SetEquals(other.Paths);

    /// <summary>Builds a readable name: the longest common file-name prefix, else the folder name.</summary>
    public static string MakeName(IReadOnlyList<Entry> entries)
    {
        var names = entries.Select(e => Path.GetFileNameWithoutExtension(e.Path)).ToList();
        if (names.Count == 0) return "Set";
        var prefix = names[0];
        foreach (var n in names.Skip(1))
            while (prefix.Length > 0 && !n.StartsWith(prefix, StringComparison.Ordinal)) prefix = prefix[..^1];
        prefix = prefix.Trim('-', '_', ' ', '.');
        if (prefix.Length >= 3) return prefix;
        var folder = Path.GetDirectoryName(entries[0].Path) ?? "";
        var leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(leaf) ? "Set" : leaf;
    }

    public JsonObject ToJson()
    {
        var entries = new JsonArray();
        foreach (var e in Entries) entries.Add(new JsonObject { ["kind"] = SettingsJson.V(e.Kind), ["path"] = SettingsJson.V(e.Path) });
        var o = new JsonObject { ["id"] = SettingsJson.V(Id), ["name"] = SettingsJson.V(Name), ["entries"] = entries, ["date"] = SettingsJson.V(Date) };
        if (State != null) o["state"] = State.ToJson();
        return o;
    }

    public static RecentSet FromJson(JsonObject o)
    {
        var s = new RecentSet { Id = SettingsJson.G(o, "id"), Name = SettingsJson.S(o, "name", "Set"), Date = SettingsJson.Date(o, "date", DateTime.UtcNow) };
        if (SettingsJson.Arr(o, "entries") is JsonArray a)
            foreach (var e in a.OfType<JsonObject>())
                if (ChannelKinds.FromRawValue(SettingsJson.S(e, "kind", "")) is ChannelKind k && SettingsJson.S(e, "path", "") is { Length: > 0 } p) s.Entries.Add(new Entry(k, p));
        if (SettingsJson.Obj(o, "state") is JsonObject st) s.State = ProjectDocument.FromJson(st);
        return s;
    }
}

/// <summary>Persistent most-recently-used list of channel sets (preferences backed).</summary>
public sealed class RecentSetsStore
{
    public const string Key = "recentChannelSets";
    public const int Limit = 12;
    readonly Preferences prefs;

    public RecentSetsStore(Preferences preferences) { prefs = preferences; }

    public List<RecentSet> Load()
    {
        if (prefs.GetNode(Key) is not JsonArray a) return new List<RecentSet>();
        try { return a.OfType<JsonObject>().Select(RecentSet.FromJson).ToList(); } catch { return new List<RecentSet>(); }
    }

    public void Save(List<RecentSet> sets)
    {
        var a = new JsonArray();
        foreach (var s in sets) a.Add(s.ToJson());
        prefs.SetNode(Key, a);
    }

    /// <summary>Inserts (or moves to the front) a set with these entries. Same file paths = same set.</summary>
    public List<RecentSet> Record(IReadOnlyList<RecentSet.Entry> entries)
    {
        if (entries.Count == 0) return Load();
        var sorted = entries.OrderBy(e => e.Kind.RawValue(), StringComparer.Ordinal).ToList();
        var paths = sorted.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = Load();
        var previous = existing.FirstOrDefault(s => s.Paths.SetEquals(paths));
        var sets = existing.Where(s => !s.Paths.SetEquals(paths)).ToList();
        // Same files again: keep the entry's identity and its saved state.
        sets.Insert(0, new RecentSet { Id = previous?.Id ?? Guid.NewGuid(), Name = RecentSet.MakeName(sorted), Entries = sorted, Date = DateTime.UtcNow, State = previous?.State });
        if (sets.Count > Limit) sets.RemoveRange(Limit, sets.Count - Limit);
        Save(sets);
        return sets;
    }

    /// <summary>Stores the state for the set with exactly these file paths (no-op when the set is unknown).</summary>
    public List<RecentSet> UpdateState(HashSet<string> paths, ProjectDocument state)
    {
        var sets = Load();
        int i = sets.FindIndex(s => s.Paths.SetEquals(paths));
        if (i < 0) return sets;
        sets[i].State = state;
        Save(sets);
        return sets;
    }

    public List<RecentSet> Remove(Guid id)
    {
        var sets = Load().Where(s => s.Id != id).ToList();
        Save(sets);
        return sets;
    }

    public void Clear() => Save(new List<RecentSet>());
}
