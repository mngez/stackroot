using System.Diagnostics;
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
    private const string CaCert = "stackroot-ca.crt";
    private const string CaKey = "stackroot-ca.key";
    private const string Manifest = "dev-domains.json";
    private const int ServerRenewalLeadDays = 30;
    private const int CaRenewalLeadDays = 365;

    public static DevSslPaths? EnsureDevSslCertificate(
        StackrootPaths paths,
        IEnumerable<string> domains)
    {
        MigrateMisplacedSslMaterial(paths);

        var uniqueDomains = domains
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Select(static d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniqueDomains.Count == 0)
        {
            return null;
        }

        var confDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf");
        var sslDir = Path.Combine(confDir, "ssl");
        Directory.CreateDirectory(sslDir);

        var certAbs = Path.Combine(sslDir, DevCert);
        var keyAbs = Path.Combine(sslDir, DevKey);
        var caAbs = Path.Combine(sslDir, CaCert);
        var caKeyAbs = Path.Combine(sslDir, CaKey);
        var manifestAbs = Path.Combine(sslDir, Manifest);
        var fingerprint = DomainFingerprint(uniqueDomains);

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

        if (manifestMatches &&
            File.Exists(caAbs) &&
            File.Exists(caKeyAbs) &&
            !IsCertificateExpiringSoon(caAbs, CaRenewalLeadDays) &&
            IsCertificateExpiringSoon(certAbs, ServerRenewalLeadDays) &&
            DevSslDotNetCertificateGenerator.TryRenewServerCertificate(sslDir, renewalDomains))
        {
            WriteManifest(manifestAbs, fingerprint, renewalDomains, generator: manifest?.Generator ?? "dotnet");
            return TryGetExisting(paths);
        }

        if (DevSslDotNetCertificateGenerator.TryGenerate(sslDir, uniqueDomains))
        {
            WriteManifest(manifestAbs, fingerprint, uniqueDomains, generator: "dotnet");
            return TryGetExisting(paths);
        }

        return TryGetExisting(paths);
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

    private static void WriteManifest(string manifestAbs, string fingerprint, IReadOnlyList<string> domains, string generator)
    {
        var payload = new DevSslManifest
        {
            Fingerprint = fingerprint,
            Domains = domains.ToList(),
            CaSigned = true,
            Generator = generator
        };
        File.WriteAllText(manifestAbs, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    public static DevSslTrustResult TrustDevSslCertificate(StackrootPaths paths)
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
        if (!string.IsNullOrWhiteSpace(thumbprint) && IsTrustedInWindows(thumbprint))
        {
            return new DevSslTrustResult(true, "Local CA is already trusted.");
        }

        var addResult = RunProcess("certutil", ["-addstore", "-user", "Root", caPath]);
        if (addResult.ExitCode != 0)
        {
            var detail = FirstNonEmpty(addResult.Error, addResult.Output, "certutil failed to install the CA.");
            return new DevSslTrustResult(false, detail);
        }

        return new DevSslTrustResult(true, "Local CA installed to Windows trusted roots.");
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
        return new DevSslPaths(
            "ssl/dev.crt",
            "ssl/dev.key",
            certAbs,
            keyAbs,
            File.Exists(caAbs) ? caAbs : null);
    }

    private static string DomainFingerprint(IReadOnlyList<string> domains)
    {
        var ordered = domains.OrderBy(static d => d, StringComparer.Ordinal).ToArray();
        var input = string.Join('\n', ordered);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
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
            var cert = new X509Certificate2(certPath);
            return cert.Thumbprint?.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTrustedInWindows(string thumbprint)
    {
        var storeResult = RunProcess("certutil", ["-store", "-user", "Root"]);
        if (storeResult.ExitCode != 0)
        {
            return false;
        }

        return (storeResult.Output ?? string.Empty).Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
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

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
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
        public bool? CaSigned { get; init; }
        public string? Generator { get; init; }
    }
}
