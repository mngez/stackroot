using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;

namespace Stackroot.Core.Supervisor;

public sealed class GlobalProcessStore
{
    private readonly Stackroot.Core.IO.Storage.IJsonFileStore _jsonFileStore;

    public GlobalProcessStore(string dataRoot, Stackroot.Core.IO.Storage.IJsonFileStore? jsonFileStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _jsonFileStore = jsonFileStore ?? new Stackroot.Core.IO.Storage.JsonFileStore();
        FilePath = StackrootPathResolver.ProcessesRegistryPath(dataRoot);
    }

    public string FilePath { get; }

    public GlobalProcessesRegistry Load()
    {
        var registry = _jsonFileStore.Load(FilePath, () => new GlobalProcessesRegistry());
        var normalized = registry with
        {
            SchemaVersion = 1,
            Processes = registry.Processes ?? []
        };

        return normalized;
    }

    public void Save(GlobalProcessesRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var normalized = registry with
        {
            SchemaVersion = 1,
            Processes = registry.Processes ?? []
        };

        _jsonFileStore.Save(FilePath, normalized);
    }

    public IReadOnlyList<GlobalProcess> List() => Load().Processes;

    public GlobalProcess? GetById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Load().Processes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public GlobalProcess Upsert(GlobalProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ValidateProcess(process);

        var normalized = Normalize(process);
        var registry = Load();
        var index = registry.Processes.FindIndex(p => string.Equals(p.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            registry.Processes[index] = normalized;
        }
        else
        {
            registry.Processes.Add(normalized);
        }

        Save(registry);
        return normalized;
    }

    public GlobalProcess? Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var registry = Load();
        var existing = registry.Processes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return null;
        }

        registry.Processes.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(registry);
        return existing;
    }

    private static GlobalProcess Normalize(GlobalProcess process)
    {
        var id = string.IsNullOrWhiteSpace(process.Id) ? Slugify(process.Name) : process.Id.Trim();
        var name = string.IsNullOrWhiteSpace(process.Name) ? id : process.Name.Trim();
        var workDir = process.WorkDir?.Trim() ?? string.Empty;
        var cwd = string.IsNullOrWhiteSpace(process.Cwd) ? "." : process.Cwd.Trim();
        var argv = process.Argv.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();

        return process with
        {
            Id = id,
            Name = name,
            WorkDir = workDir,
            Cwd = cwd,
            Argv = argv,
            RestartDelaySeconds = process.RestartDelaySeconds is int delay and > 0
                ? Math.Min(delay, 86_400)
                : null
        };
    }

    private static void ValidateProcess(GlobalProcess process)
    {
        if (string.IsNullOrWhiteSpace(process.Name) && string.IsNullOrWhiteSpace(process.Id))
        {
            throw new InvalidOperationException("Process id or name is required.");
        }

        if (process.Argv is null || process.Argv.Count == 0 || string.IsNullOrWhiteSpace(process.Argv[0]))
        {
            throw new InvalidOperationException("Process argv must include the executable as the first argument.");
        }
    }

    private static string Slugify(string value) =>
        string.Join('-', value
            .Trim()
            .ToLowerInvariant()
            .Split([' ', '-', '_', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries));
}
