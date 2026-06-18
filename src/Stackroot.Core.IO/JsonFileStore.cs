using System.Text.Json;

namespace Stackroot.Core.IO;

public sealed class JsonFileStore
{
    private readonly JsonSerializerOptions _options;

    public JsonFileStore(JsonSerializerOptions? options = null)
    {
        _options = options ?? JsonSerializerConfig.Default;
    }

    public T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, _options);
    }

    public void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, _options);

        File.WriteAllText(tempPath, json);

        if (File.Exists(path))
        {
            // Replace is atomic on NTFS and keeps destination metadata consistent.
            File.Replace(tempPath, path, null);
            return;
        }

        File.Move(tempPath, path);
    }
}
