using System.Text.Json;
using Stackroot.Core.IO;

namespace Stackroot.Core.IO.Storage;

public sealed class JsonFileStore : IJsonFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerConfig.Default;

    public T Load<T>(string path, Func<T> fallbackFactory)
    {
        if (!File.Exists(path))
        {
            return fallbackFactory();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? fallbackFactory();
        }
        catch (Exception ex)
        {
            var backupPath = TryBackupUnreadableFile(path);
            var backupMessage = string.IsNullOrWhiteSpace(backupPath)
                ? string.Empty
                : $" A backup was saved to '{backupPath}'.";
            throw new InvalidDataException($"Could not read JSON file '{path}'.{backupMessage}", ex);
        }
    }

    public void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var payload = JsonSerializer.Serialize(value, SerializerOptions);
        File.WriteAllText(tempPath, payload);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
            return;
        }

        File.Move(tempPath, path);
    }

    private static string? TryBackupUnreadableFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var backupPath = $"{path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }
}
