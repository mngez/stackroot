using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases.Models;
using Stackroot.Core.IO;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Databases;

public sealed class DatabaseManager
{
    private readonly DatabaseRegistryStore _registryStore;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _installRegistry;
    private readonly string _dataRoot;

    public DatabaseManager(
        StackrootPaths paths,
        DatabaseRegistryStore registryStore,
        SettingsStore settingsStore,
        InstallRegistryStore installRegistry)
    {
        _dataRoot = paths.DataRoot;
        _registryStore = registryStore;
        _settingsStore = settingsStore;
        _installRegistry = installRegistry;
    }

    public IReadOnlyList<DatabaseRecord> List()
    {
        return _registryStore.Load().Databases
            .OrderBy(database => database.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<SqlEngine> ListEngines() => [SqlEngine.Mysql, SqlEngine.Mariadb, SqlEngine.Postgresql, SqlEngine.Mongodb];

    public DatabaseRecord Create(string name, SqlEngine? engine = null, string? siteId = null)
    {
        var normalizedName = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Database name is required.", nameof(name));
        }

        var selectedEngine = ResolveEngine(engine);
        var normalizedSiteId = string.IsNullOrWhiteSpace(siteId) ? null : siteId.Trim();
        var settings = _settingsStore.Load();
        var registry = _registryStore.Load();
        if (registry.Databases.Any(database => string.Equals(database.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Database '{normalizedName}' already exists.");
        }

        CreateOnServer(settings, selectedEngine, normalizedName);

        var now = DateTimeOffset.UtcNow.ToString("O");
        var record = new DatabaseRecord
        {
            Name = normalizedName,
            Engine = selectedEngine,
            SiteId = normalizedSiteId,
            CreatedAt = now,
            UpdatedAt = now
        };

        registry.Databases.Add(record);
        _registryStore.Save(registry);
        return record;
    }

    public bool Delete(string name, bool dropFromServer = true)
    {
        var normalizedName = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        var registry = _registryStore.Load();
        var record = registry.Databases.FirstOrDefault(
            database => string.Equals(database.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return false;
        }

        if (dropFromServer)
        {
            try
            {
                var settings = _settingsStore.Load();
                DropOnServer(settings, record.Engine, record.Name);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not running"))
            {
                throw new InvalidOperationException(
                    $"Cannot delete '{normalizedName}' — the {record.Engine} service is not running. Start it from Services first.");
            }
        }

        registry.Databases.RemoveAll(
            database => string.Equals(database.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        _registryStore.Save(registry);
        return true;
    }

    public void UnlinkSite(string siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return;
        }

        var registry = _registryStore.Load();
        var changed = false;
        for (var index = 0; index < registry.Databases.Count; index++)
        {
            var database = registry.Databases[index];
            if (!string.Equals(database.SiteId, siteId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            registry.Databases[index] = database with
            {
                SiteId = null,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
            changed = true;
        }

        if (changed)
        {
            _registryStore.Save(registry);
        }
    }

    public void LinkToSite(string databaseName, string siteId)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(siteId))
            return;

        var registry = _registryStore.Load();
        for (var i = 0; i < registry.Databases.Count; i++)
        {
            if (string.Equals(registry.Databases[i].Name, databaseName, StringComparison.OrdinalIgnoreCase))
            {
                registry.Databases[i] = registry.Databases[i] with
                {
                    SiteId = siteId.Trim(),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
                _registryStore.Save(registry);
                return;
            }
        }
    }

    public string Backup(string name, string? destinationDirectory = null)
    {
        var normalizedName = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Database name is required.", nameof(name));
        }

        var registry = _registryStore.Load();
        var record = registry.Databases.FirstOrDefault(
            database => string.Equals(database.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            throw new KeyNotFoundException($"Database '{normalizedName}' was not found.");
        }

        var settings = _settingsStore.Load();

        var backupsDirectory = string.IsNullOrWhiteSpace(destinationDirectory)
            ? StackrootPathResolver.DatabaseBackupsPath(_dataRoot)
            : destinationDirectory;
        Directory.CreateDirectory(backupsDirectory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var ext = record.Engine == SqlEngine.Mongodb ? ".archive" : ".sql";
        var fileName = $"{record.Name}-{record.Engine.ToString().ToLowerInvariant()}-{timestamp}{ext}";
        var backupPath = Path.Combine(backupsDirectory, fileName);
        ExportToServer(settings, record.Engine, record.Name, backupPath);

        var now = DateTimeOffset.UtcNow.ToString("O");
        registry.Databases.RemoveAll(database => string.Equals(database.Name, record.Name, StringComparison.OrdinalIgnoreCase));
        registry.Databases.Add(record with
        {
            UpdatedAt = now,
            LastBackupAt = now
        });
        _registryStore.Save(registry);

        return backupPath;
    }

    public IReadOnlyList<DatabaseBackupInfo> ListBackups(string? databaseName = null)
    {
        var backupsDirectory = StackrootPathResolver.DatabaseBackupsPath(_dataRoot);
        var diagPath = Path.Combine(_dataRoot, "logs", "backups-diag.log");
        void Diag(string msg)
        {
            try { File.AppendAllText(diagPath, $"[{DateTimeOffset.UtcNow:O}] {msg}\n"); } catch { }
        }

        Diag($"ListBackups called: dir={backupsDirectory}, filter={databaseName ?? "(null)"}, exists={Directory.Exists(backupsDirectory)}");

        if (!Directory.Exists(backupsDirectory))
        {
            return [];
        }

        var filter = string.IsNullOrWhiteSpace(databaseName)
            ? null
            : NormalizeName(databaseName);

        var sqlFiles = Directory.EnumerateFiles(backupsDirectory, "*.sql", SearchOption.TopDirectoryOnly);
        var archiveFiles = Directory.EnumerateFiles(backupsDirectory, "*.archive", SearchOption.TopDirectoryOnly);
        var allFiles = sqlFiles.Concat(archiveFiles).ToList();
        Diag($"Found {allFiles.Count} backup file(s) in directory (.sql + .archive)");

        var results = allFiles
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var info = new FileInfo(path);
                ParseBackupFileName(fileName, out var dbName, out var engine);
                Diag($"  File: {fileName} -> dbName='{dbName}' engine={engine}");
                return new DatabaseBackupInfo(
                    fileName,
                    path,
                    info.Length,
                    info.LastWriteTimeUtc,
                    dbName,
                    engine);
            })
            .Where(backup => filter is null ||
                             string.Equals(backup.DatabaseName, filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(backup => backup.CreatedAt)
            .ToList();

        Diag($"After filter (filter='{filter}'): {results.Count} result(s)");
        return results;
    }

    public string RestoreBackup(string backupPath, string? targetDatabaseName = null)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file was not found.", backupPath);
        }

        var fileName = Path.GetFileName(backupPath);
        ParseBackupFileName(fileName, out var parsedName, out var parsedEngine);
        var databaseName = NormalizeName(targetDatabaseName ?? parsedName ?? Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("Could not determine the target database name.", nameof(targetDatabaseName));
        }

        var engine = parsedEngine ?? ResolveEngine(null);
        var registry = _registryStore.Load();
        if (!registry.Databases.Any(db => string.Equals(db.Name, databaseName, StringComparison.OrdinalIgnoreCase)))
        {
            Create(databaseName, engine);
        }

        var settings = _settingsStore.Load();
        ImportToServer(settings, engine, databaseName, backupPath);

        var now = DateTimeOffset.UtcNow.ToString("O");
        registry = _registryStore.Load();
        var record = registry.Databases.First(db => string.Equals(db.Name, databaseName, StringComparison.OrdinalIgnoreCase));
        registry.Databases.RemoveAll(db => string.Equals(db.Name, databaseName, StringComparison.OrdinalIgnoreCase));
        registry.Databases.Add(record with
        {
            UpdatedAt = now,
            LastBackupAt = record.LastBackupAt ?? now
        });
        _registryStore.Save(registry);

        return databaseName;
    }

    private static void ParseBackupFileName(string fileName, out string? databaseName, out SqlEngine? engine)
    {
        databaseName = null;
        engine = null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Format: {dbName}-{engine}-{yyyyMMdd}-{HHmmss}.sql
        // Parse from right: last 2 are date+time, 3rd-last is engine, rest is db name
        if (parts.Length >= 4)
        {
            // Try engine at parts[^3]; if not a known engine, assume old format (parts[0] = db, parts[1] = engine)
            if (Enum.TryParse<SqlEngine>(parts[^3], ignoreCase: true, out var parsedEngine))
            {
                engine = parsedEngine;
                databaseName = string.Join("-", parts[..^3]);
            }
            else if (parts.Length >= 3 && Enum.TryParse<SqlEngine>(parts[1], ignoreCase: true, out var fallbackEngine))
            {
                // Old format: {singleName}-{engine}-{...}
                engine = fallbackEngine;
                databaseName = parts[0];
            }
        }
        else if (parts.Length >= 3)
        {
            // Old format fallback
            databaseName = parts[0];
            if (Enum.TryParse<SqlEngine>(parts[1], ignoreCase: true, out var parsedEngine))
                engine = parsedEngine;
        }
    }

    public string BuildEnvSnippet(string databaseName, SqlEngine? engine = null)
    {
        var resolvedEngine = ResolveEngine(engine);
        var settings = _settingsStore.Load();
        var (credentials, serviceKey, defaultPort, connectionName) = resolvedEngine switch
        {
            SqlEngine.Mysql => (settings.Databases.Mysql, ServiceId.Mysql, 3306, "mysql"),
            SqlEngine.Mariadb => (settings.Databases.Mariadb, ServiceId.Mariadb, 3306, "mysql"),
            SqlEngine.Postgresql => (settings.Databases.Postgresql, ServiceId.Postgresql, 5432, "pgsql"),
            SqlEngine.Mongodb => (settings.Databases.Mongodb, ServiceId.Mongodb, 27017, "mongodb"),
            _ => (settings.Databases.Mysql, ServiceId.Mysql, 3306, "mysql")
        };
        var port = settings.Services.TryGetValue(serviceKey, out var service) ? service.Port : defaultPort;

        var builder = new StringBuilder();
        builder.AppendLine($"DB_CONNECTION={connectionName}");
        builder.AppendLine("DB_HOST=127.0.0.1");
        builder.AppendLine($"DB_PORT={port}");
        builder.AppendLine($"DB_DATABASE={NormalizeName(databaseName)}");
        if (!string.IsNullOrWhiteSpace(credentials.Username))
        {
            builder.AppendLine($"DB_USERNAME={credentials.Username}");
        }
        if (!string.IsNullOrWhiteSpace(credentials.Password))
        {
            builder.Append($"DB_PASSWORD={credentials.Password}");
        }
        else if (builder.ToString().EndsWith('\n'))
        {
            builder.Length -= Environment.NewLine.Length;
        }
        return builder.ToString();
    }

    public IReadOnlyDictionary<SqlEngine, string> BuildEnvSnippets(string databaseName)
    {
        var dict = new Dictionary<SqlEngine, string>();
        foreach (var engine in ListEngines())
        {
            dict[engine] = BuildEnvSnippet(databaseName, engine);
        }
        return dict;
    }

    private void CreateOnServer(AppSettings settings, SqlEngine engine, string name)
    {
        switch (engine)
        {
            case SqlEngine.Mysql or SqlEngine.Mariadb:
                MysqlDatabaseClient.CreateDatabase(_installRegistry, settings, engine, name);
                break;
            case SqlEngine.Postgresql:
                PostgreSqlDatabaseClient.CreateDatabase(_installRegistry, settings, name);
                break;
            case SqlEngine.Mongodb:
                MongoDatabaseClient.CreateDatabase(_installRegistry, settings, name);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unsupported database engine.");
        }
    }

    private void DropOnServer(AppSettings settings, SqlEngine engine, string name)
    {
        switch (engine)
        {
            case SqlEngine.Mysql or SqlEngine.Mariadb:
                MysqlDatabaseClient.DropDatabase(_installRegistry, settings, engine, name);
                break;
            case SqlEngine.Postgresql:
                PostgreSqlDatabaseClient.DropDatabase(_installRegistry, settings, name);
                break;
            case SqlEngine.Mongodb:
                MongoDatabaseClient.DropDatabase(_installRegistry, settings, name);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unsupported database engine.");
        }
    }

    private void ExportToServer(AppSettings settings, SqlEngine engine, string name, string backupPath)
    {
        switch (engine)
        {
            case SqlEngine.Mysql or SqlEngine.Mariadb:
                MysqlDatabaseClient.EnsureRootPassword(_installRegistry, settings, engine);
                MysqlDatabaseClient.ExportSqlFile(_installRegistry, settings, engine, name, backupPath);
                break;
            case SqlEngine.Postgresql:
                PostgreSqlDatabaseClient.ExportDump(_installRegistry, settings, name, backupPath);
                break;
            case SqlEngine.Mongodb:
                MongoDatabaseClient.ExportDump(_installRegistry, settings, name, backupPath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unsupported database engine.");
        }
    }

    private void ImportToServer(AppSettings settings, SqlEngine engine, string name, string backupPath)
    {
        switch (engine)
        {
            case SqlEngine.Mysql or SqlEngine.Mariadb:
                MysqlDatabaseClient.EnsureRootPassword(_installRegistry, settings, engine);
                MysqlDatabaseClient.ImportSqlFile(_installRegistry, settings, engine, name, backupPath);
                break;
            case SqlEngine.Postgresql:
                PostgreSqlDatabaseClient.ImportDump(_installRegistry, settings, name, backupPath);
                break;
            case SqlEngine.Mongodb:
                MongoDatabaseClient.ImportDump(_installRegistry, settings, name, backupPath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unsupported database engine.");
        }
    }

    private SqlEngine ResolveEngine(SqlEngine? engine)
    {
        if (engine is not null)
        {
            return engine.Value;
        }

        var settings = _settingsStore.Load();
        return settings.Databases.ActiveSqlEngine ?? SqlEngine.Mysql;
    }

    private static string NormalizeName(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return new string(trimmed.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' ? ch : '_').ToArray());
    }
}
