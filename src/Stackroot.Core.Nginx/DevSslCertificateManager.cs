using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Nginx;

public sealed record DevSslPaths(
    string CertRel,
    string KeyRel,
    string CertAbs,
    string KeyAbs,
    string? CaAbs);

public sealed record DevSslTrustResult(bool Ok, string? Message = null);

public static class DevSslCertificateManager
{
    private const string DevCert = "dev.crt";
    private const string DevKey = "dev.key";
    private const string DevFullChainCert = "dev-fullchain.crt";
    private const string CaCert = "stackroot-ca.crt";
    private const string CaKey = "stackroot-ca.key";
    private const string Manifest = "dev-domains.json";
    private const int ServerRenewalLeadDays = 30;
    private const int CaRenewalLeadDays = 365;
    private const string LocalCaCommonName = "Stackroot Local CA";

    // Prevents concurrent cert generation from racing on the same ssl directory.
    private static readonly SemaphoreSlim _certGenLock = new(1, 1);

    // Short-lived cache to avoid enumerating the Windows Root store on every poll.
    private static readonly object _trustedCaCacheLock = new();
    private static IReadOnlyList<string>? _trustedCaCache;
    private static DateTimeOffset _trustedCaCacheAt;

    public const string NginxSslCertificateRel = "ssl/dev-fullchain.crt";
    public const string NginxSslCertificateKeyRel = "ssl/dev.key";

    public static DevSslPaths? EnsureDevSslCertificate(
        StackrootPaths paths,
        IEnumerable<string> domains,
        IEnumerable<string>? ipAddresses = null)
    {
        MigrateMisplacedSslMaterial(paths);

        var uniqueDomains = domains
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Select(static d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var uniqueIpAddresses = NormalizeIpAddresses(ipAddresses);

        if (uniqueDomains.Count == 0 && uniqueIpAddresses.Count == 0)
        {
            return null;
        }

        if (uniqueDomains.Count == 0)
        {
            uniqueDomains.Add("localhost");
        }

        var confDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf");
        var sslDir = Path.Combine(confDir, "ssl");
        Directory.CreateDirectory(sslDir);

        var certAbs = Path.Combine(sslDir, DevCert);
        var keyAbs = Path.Combine(sslDir, DevKey);
        var caAbs = Path.Combine(sslDir, CaCert);
        var caKeyAbs = Path.Combine(sslDir, CaKey);
        var manifestAbs = Path.Combine(sslDir, Manifest);
        var fingerprint = SanFingerprint(uniqueDomains, uniqueIpAddresses);

        _certGenLock.Wait();
        try
        {
            var hasMaterial = File.Exists(certAbs) && File.Exists(keyAbs);
            var manifestMatches = TryReadManifest(manifestAbs, out var manifest) &&
                string.Equals(manifest?.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
                manifest?.CaSigned != false;

            if (manifestMatches && hasMaterial && !NeedsRenewal(certAbs, caAbs))
            {
                return TryGetExisting(paths);
            }

            var renewalDomains = manifest?.Domains is { Count: > 0 } manifestDomains
                ? manifestDomains
                : uniqueDomains;
            var renewalIpAddresses = manifest?.IpAddresses is { Count: > 0 } manifestIpAddresses
                ? manifestIpAddresses
                : uniqueIpAddresses;

            if (manifestMatches &&
                File.Exists(caAbs) &&
                File.Exists(caKeyAbs) &&
                !IsCertificateExpiringSoon(caAbs, CaRenewalLeadDays) &&
                IsCertificateExpiringSoon(certAbs, ServerRenewalLeadDays) &&
                DevSslDotNetCertificateGenerator.TryRenewServerCertificate(sslDir, renewalDomains, renewalIpAddresses))
            {
                WriteManifest(manifestAbs, fingerprint, renewalDomains, renewalIpAddresses, generator: manifest?.Generator ?? "dotnet");
                EnsureFullChainPem(sslDir);
                return TryGetExisting(paths);
            }

            if (DevSslDotNetCertificateGenerator.TryGenerate(sslDir, uniqueDomains, uniqueIpAddresses))
            {
                WriteManifest(manifestAbs, fingerprint, uniqueDomains, uniqueIpAddresses, generator: "dotnet");
                EnsureFullChainPem(sslDir);
                PruneStaleTrustedLocalCas(GetLocalCaThumbprint(paths));
                return TryGetExisting(paths);
            }

            return TryGetExisting(paths);
        }
        finally
        {
            _certGenLock.Release();
        }
    }

    public static bool CertificatesExist(StackrootPaths paths)
    {
        var existing = TryGetExisting(paths);
        if (existing is null)
        {
            return false;
        }

        return !IsCertificateExpiringSoon(existing.CertAbs, leadDays: 0);
    }

    public static bool IsLocalCaTrusted(StackrootPaths paths)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var thumbprint = GetLocalCaThumbprint(paths);
        return !string.IsNullOrWhiteSpace(thumbprint) && IsTrustedInWindows(thumbprint);
    }

    public static string? GetLocalCaThumbprint(StackrootPaths paths)
    {
        var confDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf");
        var caPath = Path.Combine(confDir, "ssl", CaCert);
        if (!File.Exists(caPath))
        {
            return null;
        }

        return ReadThumbprint(caPath);
    }

    public static bool ShouldPromptForLocalCaTrust(StackrootPaths paths)
        => CertificatesExist(paths) && !IsLocalCaTrusted(paths);

    public static DevSslPaths? TryGetExisting(StackrootPaths paths)
    {
        MigrateMisplacedSslMaterial(paths);
        var confDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf");
        return Existing(confDir);
    }

    private static void MigrateMisplacedSslMaterial(StackrootPaths paths)
    {
        var correctSslDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf", "ssl");
        var legacySslDir = Path.Combine(paths.ConfigRoot, "config", "nginx", "conf", "ssl");
        if (!Directory.Exists(legacySslDir))
        {
            return;
        }

        var legacyCert = Path.Combine(legacySslDir, DevCert);
        var correctCert = Path.Combine(correctSslDir, DevCert);
        if (!File.Exists(legacyCert) || File.Exists(correctCert))
        {
            return;
        }

        Directory.CreateDirectory(correctSslDir);
        foreach (var file in Directory.EnumerateFiles(legacySslDir))
        {
            var destination = Path.Combine(correctSslDir, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Copy(file, destination);
            }
        }
    }

    private static bool NeedsRenewal(string serverCertPath, string caCertPath)
    {
        if (!File.Exists(serverCertPath))
        {
            return true;
        }

        if (IsCertificateExpiringSoon(serverCertPath, ServerRenewalLeadDays))
        {
            return true;
        }

        return File.Exists(caCertPath) && IsCertificateExpiringSoon(caCertPath, CaRenewalLeadDays);
    }

    private static bool IsCertificateExpiringSoon(string certPath, int leadDays)
    {
        if (!File.Exists(certPath))
        {
            return true;
        }

        try
        {
            using var cert = new X509Certificate2(certPath);
            return DateTime.UtcNow.AddDays(leadDays) >= cert.NotAfter;
        }
        catch
        {
            return true;
        }
    }

    private static void WriteManifest(
        string manifestAbs,
        string fingerprint,
        IReadOnlyList<string> domains,
        IReadOnlyList<string> ipAddresses,
        string generator)
    {
        var payload = new DevSslManifest
        {
            Fingerprint = fingerprint,
            Domains = domains.ToList(),
            IpAddresses = ipAddresses.ToList(),
            CaSigned = true,
            Generator = generator
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var tmpManifest = manifestAbs + ".tmp";
        File.WriteAllText(tmpManifest, json, Encoding.UTF8);
        File.Move(tmpManifest, manifestAbs, overwrite: true);
    }

    public static DevSslTrustResult TrustDevSslCertificate(StackrootPaths paths, bool machineWide = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DevSslTrustResult(false, "Automatic trust is currently supported on Windows only.");
        }

        var confDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf");
        var caPath = Path.Combine(confDir, "ssl", CaCert);
        if (!File.Exists(caPath))
        {
            return new DevSslTrustResult(false, "Local CA not found. Regenerate sites first.");
        }

        var thumbprint = ReadThumbprint(caPath);
        var removed = PruneStaleTrustedLocalCas(thumbprint);
        if (!string.IsNullOrWhiteSpace(thumbprint) && IsTrustedInWindows(thumbprint))
        {
            return removed > 0
                ? new DevSslTrustResult(
                    true,
                    $"Removed {removed} outdated Stackroot CA certificate(s). The current CA is already trusted.")
                : new DevSslTrustResult(true, "Local CA is already trusted.");
        }

        var addResult = machineWide
            ? TryInstallCaToMachineRoot(caPath)
            : TryInstallCaToUserRoot(caPath);
        if (!addResult.Ok)
        {
            return new DevSslTrustResult(false, addResult.Message);
        }

        var scope = machineWide ? "all users" : "current user";
        var message = removed > 0
            ? $"Local CA installed to Windows trusted roots ({scope}). Removed {removed} outdated Stackroot CA certificate(s)."
            : $"Local CA installed to Windows trusted roots ({scope}).";
        return new DevSslTrustResult(true, message);
    }

    public static DevSslTrustResult CleanupStaleLocalCaTrust(StackrootPaths paths)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DevSslTrustResult(false, "Automatic trust cleanup is currently supported on Windows only.");
        }

        var keep = GetLocalCaThumbprint(paths);
        var staleCount = CountTrustedStackrootLocalCas(paths);
        var removed = PruneStaleTrustedLocalCas(keep);
        if (removed > 0)
        {
            return new DevSslTrustResult(
                true,
                $"Removed {removed} outdated Stackroot CA certificate(s) from Windows trusted roots.");
        }

        return staleCount > 0
            ? new DevSslTrustResult(
                false,
                "Found outdated Stackroot CA certificates but could not remove them. Run Stackroot as administrator once, or remove them manually from certmgr.msc.")
            : new DevSslTrustResult(true, "No outdated Stackroot CA certificates were found in Windows trusted roots.");
    }

    public static int CountTrustedStackrootLocalCas(StackrootPaths paths)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var keep = NormalizeThumbprint(GetLocalCaThumbprint(paths));
        return ListTrustedStackrootLocalCas()
            .Count(thumbprint => !string.Equals(thumbprint, keep, StringComparison.OrdinalIgnoreCase));
    }

    private static DevSslPaths? Existing(string confDir)
    {
        var dir = Path.Combine(confDir, "ssl");
        var certAbs = Path.Combine(dir, DevCert);
        var keyAbs = Path.Combine(dir, DevKey);
        if (!File.Exists(certAbs) || !File.Exists(keyAbs))
        {
            return null;
        }

        var caAbs = Path.Combine(dir, CaCert);
        EnsureFullChainPem(dir);
        return new DevSslPaths(
            NginxSslCertificateRel,
            NginxSslCertificateKeyRel,
            certAbs,
            keyAbs,
            File.Exists(caAbs) ? caAbs : null);
    }

    private static void EnsureFullChainPem(string sslDir)
    {
        var devPath = Path.Combine(sslDir, DevCert);
        var caPath = Path.Combine(sslDir, CaCert);
        var fullChainPath = Path.Combine(sslDir, DevFullChainCert);
        if (!File.Exists(devPath) || !File.Exists(caPath))
        {
            return;
        }

        var pem = string.Concat(
            File.ReadAllText(devPath, Encoding.ASCII).TrimEnd(),
            '\n',
            File.ReadAllText(caPath, Encoding.ASCII).TrimEnd(),
            '\n');
        var tmpFullChain = fullChainPath + ".tmp";
        File.WriteAllText(tmpFullChain, pem, Encoding.ASCII);
        File.Move(tmpFullChain, fullChainPath, overwrite: true);
    }

    private static string SanFingerprint(IReadOnlyList<string> domains, IReadOnlyList<string> ipAddresses)
    {
        var entries = domains
            .Select(static d => $"dns:{d}")
            .Concat(ipAddresses.Select(static ip => $"ip:{ip}"))
            .OrderBy(static entry => entry, StringComparer.Ordinal)
            .ToArray();
        var input = string.Join('\n', entries);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<string> NormalizeIpAddresses(IEnumerable<string>? ipAddresses)
    {
        var unique = new List<string>();
        if (ipAddresses is null)
        {
            return unique;
        }

        foreach (var value in ipAddresses)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!IPAddress.TryParse(value.Trim(), out var address))
            {
                continue;
            }

            var normalized = address.ToString();
            if (!unique.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                unique.Add(normalized);
            }
        }

        return unique;
    }

    private static bool TryReadManifest(string path, out DevSslManifest? manifest)
    {
        manifest = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<DevSslManifest>(File.ReadAllText(path, Encoding.UTF8));
            return manifest is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadThumbprint(string certPath)
    {
        try
        {
            using var cert = new X509Certificate2(certPath);
            return cert.Thumbprint?.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetCachedTrustedCas()
    {
        lock (_trustedCaCacheLock)
        {
            if (_trustedCaCache is not null && DateTimeOffset.UtcNow - _trustedCaCacheAt < TimeSpan.FromSeconds(45))
            {
                return _trustedCaCache;
            }

            var result = ListTrustedStackrootLocalCas(StoreLocation.LocalMachine)
                .Concat(ListTrustedStackrootLocalCas(StoreLocation.CurrentUser))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _trustedCaCache = result;
            _trustedCaCacheAt = DateTimeOffset.UtcNow;
            return result;
        }
    }

    private static void InvalidateTrustedCaCache()
    {
        lock (_trustedCaCacheLock)
        {
            _trustedCaCache = null;
        }
    }

    private static bool IsTrustedInWindows(string thumbprint)
    {
        var normalized = NormalizeThumbprint(thumbprint);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return GetCachedTrustedCas()
            .Any(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static int PruneStaleTrustedLocalCas(string? keepThumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        return PruneStore(StoreLocation.LocalMachine, keepThumbprint)
               + PruneStore(StoreLocation.CurrentUser, keepThumbprint);
    }

    private static int PruneStore(StoreLocation location, string? keepThumbprint)
    {
        var keep = NormalizeThumbprint(keepThumbprint);
        var toRemove = new List<string>();

        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);

            foreach (var certificate in store.Certificates.Cast<X509Certificate2>())
            {
                if (!IsStackrootLocalCa(certificate))
                {
                    continue;
                }

                var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
                if (string.IsNullOrEmpty(thumbprint)
                    || (!string.IsNullOrEmpty(keep)
                        && string.Equals(thumbprint, keep, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                toRemove.Add(thumbprint);
            }
        }
        catch
        {
            return 0;
        }

        var removed = 0;
        foreach (var thumbprint in toRemove.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryRemoveCertificateFromRootStore(location, thumbprint))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryRemoveCertificateFromRootStore(StoreLocation location, string thumbprint)
    {
        if (TryRemoveCertificateViaStoreApi(location, thumbprint))
        {
            InvalidateTrustedCaCache();
            return true;
        }

        var args = location == StoreLocation.CurrentUser
            ? new[] { "-user", "-delstore", "Root", thumbprint }
            : new[] { "-delstore", "Root", thumbprint };

        if (RunProcess("certutil", args).ExitCode == 0)
        {
            InvalidateTrustedCaCache();
            return true;
        }

        if (location != StoreLocation.LocalMachine)
        {
            return false;
        }

        if (RunElevatedProcess("certutil", args).ExitCode == 0)
        {
            InvalidateTrustedCaCache();
            return true;
        }

        return false;
    }

    private static bool TryRemoveCertificateViaStoreApi(StoreLocation location, string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);

            var matches = store.Certificates
                .Cast<X509Certificate2>()
                .Where(certificate =>
                    string.Equals(
                        NormalizeThumbprint(certificate.Thumbprint),
                        thumbprint,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                return false;
            }

            foreach (var certificate in matches)
            {
                store.Remove(certificate);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ListTrustedStackrootLocalCas()
        => ListTrustedStackrootLocalCas(StoreLocation.LocalMachine)
            .Concat(ListTrustedStackrootLocalCas(StoreLocation.CurrentUser))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> ListTrustedStackrootLocalCas(StoreLocation location)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);

            return store.Certificates
                .Cast<X509Certificate2>()
                .Where(IsStackrootLocalCa)
                .Select(static certificate => NormalizeThumbprint(certificate.Thumbprint))
                .Where(static thumbprint => !string.IsNullOrEmpty(thumbprint))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsStackrootLocalCa(X509Certificate2 certificate)
    {
        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.Equals(commonName, LocalCaCommonName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return certificate.Subject.Contains(LocalCaCommonName, StringComparison.OrdinalIgnoreCase)
            || certificate.Issuer.Contains(LocalCaCommonName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static DevSslTrustResult TryInstallCaToUserRoot(string caPath)
    {
        var direct = RunProcess("certutil", ["-user", "-addstore", "Root", caPath]);
        if (direct.ExitCode == 0)
        {
            InvalidateTrustedCaCache();
            return new DevSslTrustResult(true);
        }

        var detail = FirstNonEmpty(
            direct.Error,
            direct.Output,
            "Could not install the CA to Windows trusted roots for the current user.");
        return new DevSslTrustResult(false, detail);
    }

    private static DevSslTrustResult TryInstallCaToMachineRoot(string caPath)
    {
        var direct = RunProcess("certutil", ["-addstore", "Root", caPath]);
        if (direct.ExitCode == 0)
        {
            InvalidateTrustedCaCache();
            return new DevSslTrustResult(true);
        }

        var elevated = RunElevatedProcess("certutil", ["-addstore", "Root", caPath]);
        if (elevated.ExitCode == 0)
        {
            InvalidateTrustedCaCache();
            return new DevSslTrustResult(true);
        }

        var detail = FirstNonEmpty(
            elevated.Error,
            elevated.Output,
            direct.Error,
            direct.Output,
            "Could not install the CA to Windows trusted roots. Administrator approval may be required.");
        return new DevSslTrustResult(false, detail);
    }

    private static ProcessResult RunElevatedProcess(string fileName, IReadOnlyList<string> args)
    {
        var arguments = string.Join(' ', args.Select(static arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                return new ProcessResult(1, string.Empty, $"Failed to start elevated: {fileName}");
            }

            process.WaitForExit(120_000);
            return process.ExitCode == 0
                ? new ProcessResult(0, string.Empty, null)
                : new ProcessResult(process.ExitCode, string.Empty, "Administrator approval is required to trust the certificate for all users.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(1, string.Empty, "Administrator approval was cancelled.");
        }
        catch (Exception ex)
        {
            return new ProcessResult(1, string.Empty, ex.Message);
        }
    }

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> args)
    {
        var startInfo = ProcessStreamEncoding.Create(fileName);

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult(1, string.Empty, $"Failed to start: {fileName}");
            }

            // Read both streams concurrently to prevent deadlock when output buffers fill.
            var outputTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var errorTask = Task.Run(() => process.StandardError.ReadToEnd());
            process.WaitForExit(30_000);
            var output = outputTask.Result;
            var error = errorTask.Result;
            return new ProcessResult(process.ExitCode, output.Trim(), error.Trim());
        }
        catch (Exception ex)
        {
            return new ProcessResult(1, string.Empty, ex.Message);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private sealed record ProcessResult(int ExitCode, string? Output, string? Error);

    private sealed class DevSslManifest
    {
        public string? Fingerprint { get; init; }
        public List<string>? Domains { get; init; }
        public List<string>? IpAddresses { get; init; }
        public bool? CaSigned { get; init; }
        public string? Generator { get; init; }
    }
}
