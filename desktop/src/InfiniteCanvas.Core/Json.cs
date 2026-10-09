using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfiniteCanvas.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Write and flush a temp file first, then swap it in, so a crash or power loss leaves either the old or the new file, never a half-written one.</summary>
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}
