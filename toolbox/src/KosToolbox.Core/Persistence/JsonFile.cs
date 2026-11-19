using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace KosToolbox.Core.Persistence;

public static class JsonFile
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path) where T : new() => Read(path, text => JsonSerializer.Deserialize<T>(text, Options));

    /// <summary>Same as <see cref="Load{T}(string)"/> without reflection (source-generated <see cref="KosJson"/>): for code the Native AOT KOS Engine runs.</summary>
    public static T Load<T>(string path, JsonTypeInfo<T> info) where T : new() => Read(path, text => JsonSerializer.Deserialize(text, info));

    /// <summary>Writes atomically so the file isn't corrupted if the process dies.</summary>
    public static void Save<T>(string path, T value) => Write(path, JsonSerializer.Serialize(value, Options));

    public static void Save<T>(string path, T value, JsonTypeInfo<T> info) => Write(path, JsonSerializer.Serialize(value, info));

    static T Read<T>(string path, Func<string, T?> parse) where T : new()
    {
        try { return File.Exists(path) ? parse(File.ReadAllText(path)) ?? new T() : new T(); }
        catch (Exception ex) { Log.Error($"File {path} is unreadable; using an empty value", ex); return new T(); }
    }

    static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
