using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbsPlayer.Core.Storage;

/// <summary>
/// Small key/value settings file (JSON) in the app's data folder. Plays the role UserDefaults has on Apple.
/// Every write saves the whole file; it's tiny.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly JsonObject _values;

    public SettingsStore(string path)
    {
        _path = path;
        _values = Load(path);
    }

    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new JsonObject();
    }

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            if (_values[key] is not { } node) return default;
            try { return node.Deserialize<T>(); }
            catch (JsonException) { return default; }
        }
    }

    public string? GetString(string key) => Get<string>(key);

    public void Set<T>(string key, T? value)
    {
        lock (_lock)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    public void Remove(string key) => Set<object>(key, null);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
