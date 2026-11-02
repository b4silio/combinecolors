using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombineColors.Model;

/// <summary>
/// Persistent user preferences (the role UserDefaults plays on macOS): one JSON file, written
/// shortly after every change. A test or a second instance can point at its own file.
/// </summary>
public sealed class Preferences
{
    readonly string path;
    readonly JsonObject root;
    readonly object gate = new();
    Timer? saveTimer;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CombineColors", "preferences.json");

    public Preferences(string? filePath = null)
    {
        path = filePath ?? DefaultPath;
        JsonObject? loaded = null;
        try { if (File.Exists(path)) loaded = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch { }
        root = loaded ?? new JsonObject();
    }

    public string FilePath => path;

    public string? GetString(string key) => root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public double? GetDouble(string key) => root[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    public bool? GetBool(string key) => root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public JsonNode? GetNode(string key) => root[key]?.DeepClone();
    public string[] GetStrings(string key) => root[key] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).Where(s => s != null).Select(s => s!).ToArray() : [];

    public void Set(string key, string? value) => Put(key, value == null ? null : JsonValue.Create(value));
    public void Set(string key, double value) => Put(key, JsonValue.Create(value));
    public void Set(string key, bool value) => Put(key, JsonValue.Create(value));
    public void SetStrings(string key, IEnumerable<string> values) { var a = new JsonArray(); foreach (var v in values) a.Add(JsonValue.Create(v)); Put(key, a); }
    public void SetNode(string key, JsonNode? node) => Put(key, node?.DeepClone());

    void Put(string key, JsonNode? node)
    {
        lock (gate)
        {
            if (node == null) root.Remove(key); else root[key] = node;
            saveTimer?.Dispose();
            saveTimer = new Timer(_ => Flush(), null, 300, Timeout.Infinite);
        }
    }

    /// <summary>Writes the file now.</summary>
    public void Flush()
    {
        string json;
        lock (gate) { saveTimer?.Dispose(); saveTimer = null; json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        catch { /* preferences are best effort */ }
    }

    /// <summary>Removes the file (tests).</summary>
    public static void Delete(string filePath)
    {
        try { File.Delete(filePath); } catch { }
    }
}
