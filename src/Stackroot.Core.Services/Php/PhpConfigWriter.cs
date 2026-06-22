using System.Collections.Concurrent;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services.Php;

public sealed record PhpVersionInfo(
    string Id,
    string Version,
    string InstallPath,
    bool IsActive,
    int? FastCgiPort,
    bool IsRequired);

public sealed class PhpConfigWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] PhpMyAdminExtensions =
    [
        "mbstring", "mysqli", "openssl", "curl", "zip", "gd", "fileinfo", "xml"
    ];

    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;

    public PhpConfigWriter(StackrootPaths paths, InstallRegistryStore registry)
    {
        _paths = paths;
        _registry = registry;
    }

    public string? WritePhpConfig(
        AppSettings settings,
        string? versionId = null,
        PhpConfigProfile profile = PhpConfigProfile.Default,
        string? sessionsPath = null)
    {
        var effectiveVersionId = string.IsNullOrWhiteSpace(versionId)
            ? settings.Php.ActiveVersionId
            : versionId;
        if (string.IsNullOrWhiteSpace(effectiveVersionId))
        {
            return null;
        }

        var installed = _registry.GetById(effectiveVersionId);
        if (installed is null || installed.Type != PackageType.Php)
        {
            return null;
        }

        var phpVersion = PhpVersionSettingsSanitizer.Sanitize(ResolvePhpVersionSettings(settings, effectiveVersionId));
        var extensionDir = ResolveExtensionDir(installed.InstallPath);
        var phpConfigRoot = Path.Combine(_paths.ConfigRoot, "php");
        Directory.CreateDirectory(phpConfigRoot);

        var iniPath = profile == PhpConfigProfile.PhpMyAdmin
            ? PhpConfigPaths.GetAdminToolIniPath(_paths.ConfigRoot, effectiveVersionId)
            : PhpConfigPaths.GetDefaultIniPath(_paths.ConfigRoot, effectiveVersionId);

        MigrateNestedIniIfNeeded(effectiveVersionId, iniPath, profile);

        if (profile == PhpConfigProfile.Default && phpVersion.ManageIniManually)
        {
            EnsureManualIniExists(iniPath, installed.InstallPath, effectiveVersionId);
            return iniPath;
        }

        var directives = BuildDirectives(phpVersion, extensionDir, profile, sessionsPath, _paths.LogsRoot, effectiveVersionId);

        foreach (var directive in phpVersion.IniOverrides)
        {
            directives[directive.Key] = directive.Value;
        }

        var discovered = PhpExtensionPolicy.DiscoverExtensions(installed.InstallPath).ToList();
        var enabledExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ConsiderExtension(string extension, bool userEnabled)
        {
            if (!userEnabled)
            {
                return;
            }

            if (!PhpExtensionPolicy.CanLoadExtension(installed.InstallPath, extension))
            {
                return;
            }

            enabledExtensions.Add(extension);
        }

        foreach (var extension in discovered)
        {
            var userEnabled = phpVersion.Extensions.TryGetValue(extension, out var preference)
                ? preference
                : PhpExtensionPolicy.DefaultExtensionPreference(extension, null, _registry, settings);
            if (string.Equals(extension, "opcache", StringComparison.OrdinalIgnoreCase) && !phpVersion.OpcacheEnabled)
            {
                userEnabled = false;
            }

            ConsiderExtension(extension, userEnabled);
        }

        foreach (var extension in phpVersion.Extensions)
        {
            if (!extension.Value || discovered.Contains(extension.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            ConsiderExtension(extension.Key, true);
        }

        if (profile == PhpConfigProfile.PhpMyAdmin)
        {
            foreach (var extension in PhpMyAdminExtensions)
            {
                if (PhpExtensionPolicy.CanLoadExtension(installed.InstallPath, extension))
                {
                    enabledExtensions.Add(extension);
                }
            }
        }

        var allKnownExtensions = discovered
            .Concat(phpVersion.Extensions.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            PhpIniMerge.EnsurePhpIniTemplate(_paths.ConfigRoot, installed.InstallPath, effectiveVersionId);
        }
        catch
        {
            // Optional until reset is used.
        }

        var templateContent = PhpIniMerge.ReadPhpIniTemplate(_paths.ConfigRoot, effectiveVersionId);
        var content = PhpIniMerge.EnsurePhpIniFile(iniPath, installed.InstallPath, templateContent);
        content = PhpIniMerge.ApplyPhpIniPatches(
            content,
            new PhpIniPatchOptions(directives, enabledExtensions.ToList(), allKnownExtensions));

        WriteTextIfChanged(iniPath, content);
        if (profile == PhpConfigProfile.Default && !phpVersion.ManageIniManually)
        {
            SyncPhpIniBesideBinary(installed.InstallPath, content);
        }

        return iniPath;
    }

    public IReadOnlyList<string> WriteAllPhpConfigs(AppSettings settings)
    {
        var versionIds = ListInstalledPhpVersions(settings).Select(static php => php.Id);
        return WritePhpConfigsForVersions(settings, versionIds);
    }

    public IReadOnlyList<string> WriteRequiredPhpConfigs(
        AppSettings settings,
        IReadOnlyCollection<string> requiredVersionIds)
    {
        var required = BuildRequiredVersionSet(settings, requiredVersionIds);
        return WritePhpConfigsForVersions(settings, required);
    }

    public IReadOnlyList<string> WriteRemainingPhpConfigs(
        AppSettings settings,
        IReadOnlyCollection<string> requiredVersionIds)
    {
        var required = BuildRequiredVersionSet(settings, requiredVersionIds);
        var remaining = ListInstalledPhpVersions(settings)
            .Where(php => !required.Contains(php.Id))
            .Select(static php => php.Id);
        return WritePhpConfigsForVersions(settings, remaining);
    }

    private IReadOnlyList<string> WritePhpConfigsForVersions(
        AppSettings settings,
        IEnumerable<string> versionIds)
    {
        var ids = versionIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (ids.Count == 1)
        {
            var single = WritePhpConfig(settings, ids[0]);
            return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : [single];
        }

        var paths = new ConcurrentBag<string>();
        Parallel.ForEach(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(ids.Count, Environment.ProcessorCount) },
            versionId =>
            {
                var ini = WritePhpConfig(settings, versionId);
                if (!string.IsNullOrWhiteSpace(ini))
                {
                    paths.Add(ini);
                }
            });

        return paths.ToList();
    }

    private static HashSet<string> BuildRequiredVersionSet(
        AppSettings settings,
        IReadOnlyCollection<string> requiredVersionIds)
    {
        var required = new HashSet<string>(requiredVersionIds, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(settings.Php.ActiveVersionId))
        {
            required.Add(settings.Php.ActiveVersionId);
        }

        return required;
    }

    private static void WriteTextIfChanged(string path, string content)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    public IReadOnlyList<PhpVersionInfo> ListInstalledPhpVersions(
        AppSettings settings,
        IReadOnlyCollection<string>? requiredVersionIds = null)
    {
        var listeners = PhpCgiRuntime.ActiveListeners();
        var installedPhp = _registry.List(PackageType.Php)
            .OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var required = requiredVersionIds is null
            ? null
            : new HashSet<string>(requiredVersionIds, StringComparer.OrdinalIgnoreCase);

        var output = new List<PhpVersionInfo>(installedPhp.Count);
        for (var i = 0; i < installedPhp.Count; i++)
        {
            var package = installedPhp[i];
            var plannedPort = PhpCgiPlanner.ResolvePlannedPort(settings, i);
            var port = listeners.TryGetValue(package.Id, out var activePort) ? activePort : plannedPort;
            output.Add(new PhpVersionInfo(
                package.Id,
                package.Version,
                package.InstallPath,
                string.Equals(settings.Php.ActiveVersionId, package.Id, StringComparison.OrdinalIgnoreCase),
                port,
                required?.Contains(package.Id) == true));
        }

        return output;
    }

    private void MigrateNestedIniIfNeeded(string versionId, string targetIniPath, PhpConfigProfile profile)
    {
        if (profile != PhpConfigProfile.Default || File.Exists(targetIniPath))
        {
            return;
        }

        var nested = Path.Combine(_paths.ConfigRoot, "php", versionId, "php.ini");
        if (!File.Exists(nested))
        {
            return;
        }

        File.Copy(nested, targetIniPath, overwrite: true);
        try
        {
            File.Delete(nested);
            var nestedDir = Path.GetDirectoryName(nested);
            if (!string.IsNullOrWhiteSpace(nestedDir) && Directory.Exists(nestedDir) && !Directory.EnumerateFileSystemEntries(nestedDir).Any())
            {
                Directory.Delete(nestedDir);
            }
        }
        catch
        {
            // Non-fatal.
        }
    }

    private static void SyncPhpIniBesideBinary(string installPath, string content)
    {
        var phpRoot = ResolvePhpRoot(installPath);
        WriteTextIfChanged(Path.Combine(phpRoot, "php.ini"), content);
    }

    private static string ResolvePhpRoot(string installPath)
    {
        var direct = installPath;
        if (File.Exists(Path.Combine(installPath, "php-cgi.exe")) || File.Exists(Path.Combine(installPath, "php.exe")))
        {
            return direct;
        }

        var nested = Path.Combine(installPath, "bin");
        return Directory.Exists(nested) ? nested : direct;
    }

    private static string ResolveExtensionDir(string installPath)
    {
        var direct = Path.Combine(installPath, "ext");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        var nested = Path.Combine(installPath, "bin", "ext");
        return Directory.Exists(nested) ? nested : direct;
    }

    private static PhpVersionSettings ResolvePhpVersionSettings(AppSettings settings, string versionId)
    {
        if (settings.Php.Versions is not null && settings.Php.Versions.TryGetValue(versionId, out var version))
        {
            return version;
        }

        return new PhpVersionSettings
        {
            MemoryLimit = settings.Php.MemoryLimit ?? "-1",
            MaxExecutionTime = settings.Php.MaxExecutionTime ?? "0",
            UploadMaxFilesize = settings.Php.UploadMaxFilesize ?? "512M",
            PostMaxSize = settings.Php.PostMaxSize ?? "512M",
            DisplayErrors = true,
            HideWarnings = false,
            HideDeprecated = true,
            LogErrors = true,
            Extensions = settings.Php.Extensions is null
                ? new Dictionary<string, bool>()
                : new Dictionary<string, bool>(settings.Php.Extensions),
            IniOverrides = settings.Php.IniOverrides is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(settings.Php.IniOverrides)
        };
    }

    private void EnsureManualIniExists(string iniPath, string installPath, string versionId)
    {
        if (File.Exists(iniPath))
        {
            return;
        }

        try
        {
            PhpIniMerge.EnsurePhpIniTemplate(_paths.ConfigRoot, installPath, versionId);
        }
        catch
        {
            // Optional until reset is used.
        }

        var templateContent = PhpIniMerge.ReadPhpIniTemplate(_paths.ConfigRoot, versionId);
        PhpIniMerge.EnsurePhpIniFile(iniPath, installPath, templateContent);
    }

    private static Dictionary<string, string> BuildDirectives(
        PhpVersionSettings phpVersion,
        string extensionDir,
        PhpConfigProfile profile,
        string? sessionsPath,
        string logsRoot,
        string versionId)
    {
        Directory.CreateDirectory(logsRoot);
        var errorLogPath = PhpLogPaths.ToIniPath(PhpLogPaths.GetErrorLogPath(logsRoot, versionId));

        var directives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory_limit"] = phpVersion.MemoryLimit,
            ["max_execution_time"] = phpVersion.MaxExecutionTime,
            ["upload_max_filesize"] = phpVersion.UploadMaxFilesize,
            ["post_max_size"] = phpVersion.PostMaxSize,
            ["extension_dir"] = extensionDir.Replace('\\', '/'),
            ["cgi.fix_pathinfo"] = "1",
            ["max_input_time"] = phpVersion.MaxInputTime.ToString(),
            ["max_input_vars"] = phpVersion.MaxInputVars.ToString(),
            ["default_socket_timeout"] = phpVersion.DefaultSocketTimeout.ToString(),
            ["realpath_cache_size"] = phpVersion.RealpathCacheSize,
            ["realpath_cache_ttl"] = phpVersion.RealpathCacheTtl.ToString()
        };

        if (profile == PhpConfigProfile.PhpMyAdmin)
        {
            directives["error_reporting"] = "E_ALL & ~E_DEPRECATED & ~E_STRICT & ~E_NOTICE";
            directives["display_errors"] = "Off";
            directives["display_startup_errors"] = "Off";
            directives["log_errors"] = "On";
            directives["error_log"] = errorLogPath;
            directives["output_buffering"] = "4096";
            if (!string.IsNullOrWhiteSpace(sessionsPath))
            {
                Directory.CreateDirectory(sessionsPath);
                directives["session_save_path"] = sessionsPath.Replace('\\', '/');
            }

            return directives;
        }

        var displayErrors = phpVersion.DisplayErrors != false;
        var logErrors = phpVersion.LogErrors != false;
        directives["error_reporting"] = BuildErrorReporting(phpVersion);
        directives["display_errors"] = displayErrors ? "On" : "Off";
        directives["display_startup_errors"] = displayErrors ? "On" : "Off";
        directives["log_errors"] = logErrors ? "On" : "Off";
        if (logErrors)
        {
            directives["error_log"] = errorLogPath;
        }

        if (phpVersion.OpcacheEnabled)
        {
            directives["opcache.enable"] = "1";
            directives["opcache.enable_cli"] = phpVersion.OpcacheEnableCli ? "1" : "0";
            directives["opcache.validate_timestamps"] = phpVersion.OpcacheValidateTimestamps ? "1" : "0";
            directives["opcache.revalidate_freq"] = phpVersion.OpcacheRevalidateFreq.ToString();
            directives["opcache.memory_consumption"] = phpVersion.OpcacheMemoryConsumption.ToString();
            directives["opcache.max_accelerated_files"] = phpVersion.OpcacheMaxAcceleratedFiles.ToString();
            directives["opcache.error_log"] = errorLogPath;
            directives["opcache.jit_buffer_size"] = "0";
            directives["opcache.jit"] = "0";
        }

        return directives;
    }

    private static string BuildErrorReporting(PhpVersionSettings version)
    {
        var level = "E_ALL";
        if (version.HideDeprecated != false)
        {
            level += " & ~E_DEPRECATED & ~E_STRICT";
        }

        if (version.HideWarnings == true)
        {
            level += " & ~E_WARNING";
        }

        level += " & ~E_NOTICE";
        return level;
    }
}
