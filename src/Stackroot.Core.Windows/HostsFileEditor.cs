using System.Diagnostics;
using System.Text;

namespace Stackroot.Core.Windows;

public sealed class HostsFileEditor
{
    private const string BeginMarker = "# BEGIN STACKROOT";
    private const string EndMarker = "# END STACKROOT";

    public HostsFileEditor(string? hostsPath = null)
    {
        HostsPath = hostsPath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers",
            "etc",
            "hosts");
    }

    public string HostsPath { get; }

    public string? LastError { get; private set; }

    public bool UpsertHost(string host, string ip = "127.0.0.1")
    {
        var entries = ReadManagedEntries().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        entries[host] = ip;
        return TryWriteManagedEntries(entries);
    }

    /// <summary>
    /// Replaces all managed hosts entries with the given set in a single write operation.
    /// </summary>
    public bool SyncHosts(IReadOnlyDictionary<string, string> entries)
    {
        return TryWriteManagedEntries(entries);
    }

    public bool RemoveHost(string host)
    {
        var entries = ReadManagedEntries().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (!entries.Remove(host))
        {
            return true;
        }

        return TryWriteManagedEntries(entries);
    }

    public IReadOnlyDictionary<string, string> ReadManagedEntries()
    {
        if (!File.Exists(HostsPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var text = File.ReadAllText(HostsPath);
        var block = ExtractManagedBlock(text);
        if (block is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                output[parts[1]] = parts[0];
            }
        }

        return output;
    }

    /// <summary>
    /// Removes managed hosts entries that are not in <paramref name="keepDomains"/>.
    /// </summary>
    public bool RemoveAllExcept(IReadOnlyCollection<string> keepDomains)
    {
        var entries = ReadManagedEntries().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var removed = false;
        var keep = new HashSet<string>(keepDomains, StringComparer.OrdinalIgnoreCase);

        foreach (var host in entries.Keys.ToList())
        {
            if (!keep.Contains(host))
            {
                entries.Remove(host);
                removed = true;
            }
        }

        return removed ? TryWriteManagedEntries(entries) : true;
    }

    private bool TryWriteManagedEntries(IReadOnlyDictionary<string, string> entries)
    {
        var newContent = string.Empty;
        try
        {
            var existing = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : string.Empty;
            var withoutManaged = RemoveManagedBlock(existing).TrimEnd();
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(withoutManaged))
            {
                builder.AppendLine(withoutManaged);
                builder.AppendLine();
            }

            builder.AppendLine(BeginMarker);
            foreach (var (host, ip) in entries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"{ip}\t{host}");
            }
            builder.AppendLine(EndMarker);

            newContent = builder.ToString();

            // Skip write if nothing changed (avoids unnecessary UAC prompts)
            if (existing == newContent)
            {
                LastError = null;
                return true;
            }

            File.WriteAllText(HostsPath, newContent);
            LastError = null;
            FlushDnsCache();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Attempt to grant permanent write permission, then retry
            if (TryGrantWritePermission())
            {
                try
                {
                    File.WriteAllText(HostsPath, newContent);
                    LastError = null;
                    FlushDnsCache();
                    return true;
                }
                catch
                {
                    // retry failed, fall through to error
                }
            }

            // Last resort: attempt elevation via runas (triggers UAC each time)
            if (TryElevatedWrite(entries))
            {
                LastError = null;
                FlushDnsCache();
                return true;
            }

            LastError = $"Access denied writing hosts file ({HostsPath}). Run Stackroot as administrator or update hosts manually.";
            return false;
        }
        catch (IOException ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Grants the current user permanent write permission on the hosts file using icacls.
    /// Requires elevation once. After this, all future writes succeed without UAC.
    /// </summary>
    private bool TryGrantWritePermission()
    {
        try
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var psi = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{HostsPath}\" /grant \"{user}\":W",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(8000);

            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }


    private bool TryElevatedWrite(IReadOnlyDictionary<string, string> entries)
    {
        try
        {
            var existing = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : string.Empty;
            var withoutManaged = RemoveManagedBlock(existing).TrimEnd();
            var fullContent = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(withoutManaged))
            {
                fullContent.AppendLine(withoutManaged);
                fullContent.AppendLine();
            }

            fullContent.AppendLine(BeginMarker);
            foreach (var (host, ip) in entries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                fullContent.AppendLine($"{ip}\t{host}");
            }
            fullContent.AppendLine(EndMarker);

            // Write to temp file, then move with elevated privileges
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, fullContent.ToString());

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c move /Y \"{tempFile}\" \"{HostsPath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(8000);

                return process?.ExitCode == 0;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractManagedBlock(string text)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var bodyStart = start + BeginMarker.Length;
        return text.Substring(bodyStart, end - bodyStart);
    }

    private static string RemoveManagedBlock(string text)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return text[..start];
        }

        var endExclusive = end + EndMarker.Length;
        return text.Remove(start, endExclusive - start);
    }

    public static void FlushDnsCache()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(5000);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
