using System.Text.Json;
using Stackroot.Core.IO;

namespace Stackroot.Core.IO.Storage;

public sealed class JsonFileStore : IJsonFileStore
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

        var json = ReadWithRetry(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, _options);
    }

    public T Load<T>(string path, Func<T> fallbackFactory)
    {
        if (!File.Exists(path))
        {
            return fallbackFactory();
        }

        try
        {
            var json = ReadWithRetry(path);
            return JsonSerializer.Deserialize<T>(json, _options) ?? fallbackFactory();
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

    // File.Replace briefly holds an exclusive lock; retry a few times before giving up.
    private static string ReadWithRetry(string path)
    {
        const int maxAttempts = 4;
        IOException? lastException = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25 * attempt);
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                lastException = ex;
            }
        }
        throw lastException!;
    }

    public void Save<T>(string path, T value) => WriteAtomic(path, value);

    public void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var payload = JsonSerializer.Serialize(value, _options);
        File.WriteAllText(tempPath, payload);

        // Never let a bad write replace a good file: read back what was just
        // written and confirm it parses before it's allowed anywhere near the
        // real path. A write that can't prove itself valid is deleted instead.
        try
        {
            var writtenJson = File.ReadAllText(tempPath);
            JsonSerializer.Deserialize<T>(writtenJson, _options);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            throw new InvalidDataException($"Refusing to write '{path}': the content that was written could not be read back.", ex);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
            return;
        }

        File.Move(tempPath, path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
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
