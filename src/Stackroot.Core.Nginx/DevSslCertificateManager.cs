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
    private const string CaSerial = "stackroot-ca.srl";
    private const string Manifest = "dev-domains.json";
    private const string CaName = "Stackroot Local CA";

    public static DevSslPaths? EnsureDevSslCertificate(StackrootPaths paths, IEnumerable<string> domains)
    {
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
        var manifestAbs = Path.Combine(sslDir, Manifest);
        var fingerprint = DomainFingerprint(uniqueDomains);

        if (TryReadManifest(manifestAbs, out var manifest) &&
            string.Equals(manifest?.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
            manifest?.CaSigned != false &&
            File.Exists(certAbs) &&
            File.Exists(keyAbs))
        {
            return Existing(confDir);
        }

        var openSsl = FindOpenSsl();
        if (openSsl is null)
        {
            return Existing(confDir);
        }

        if (!EnsureLocalCa(openSsl, sslDir))
        {
            return Existing(confDir);
        }

        if (!SignDevCertificate(openSsl, sslDir, uniqueDomains))
        {
            return Existing(confDir);
        }

        var payload = new DevSslManifest
        {
            Fingerprint = fingerprint,
            Domains = uniqueDomains,
            CaSigned = true
        };
        File.WriteAllText(manifestAbs, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return Existing(confDir);
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

    private static string? FindOpenSsl()
    {
        var candidates = new[]
        {
            "openssl",
            @"C:\Program Files\Git\usr\bin\openssl.exe"
        };

        foreach (var candidate in candidates)
        {
            var result = RunProcess(candidate, ["version"]);
            if (result.ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool EnsureLocalCa(string openSsl, string sslDir)
    {
        var caCert = Path.Combine(sslDir, CaCert);
        var caKey = Path.Combine(sslDir, CaKey);
        if (File.Exists(caCert) && File.Exists(caKey))
        {
            return true;
        }

        var configPath = Path.Combine(sslDir, "ca-openssl.cnf");
        File.WriteAllText(configPath, $$"""
[req]
distinguished_name = req_distinguished_name
x509_extensions = v3_ca
prompt = no

[req_distinguished_name]
CN = {{CaName}}
O = Stackroot
OU = Local Development

[v3_ca]
basicConstraints = critical, CA:TRUE
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash
""", Encoding.UTF8);

        if (RunProcess(openSsl, ["genrsa", "-out", caKey, "4096"]).ExitCode != 0)
        {
            return false;
        }

        return RunProcess(openSsl,
            [
                "req",
                "-x509",
                "-new",
                "-nodes",
                "-key",
                caKey,
                "-sha256",
                "-days",
                "8250",
                "-out",
                caCert,
                "-config",
                configPath,
                "-extensions",
                "v3_ca"
            ]).ExitCode == 0;
    }

    private static bool SignDevCertificate(string openSsl, string sslDir, IReadOnlyList<string> domains)
    {
        var certAbs = Path.Combine(sslDir, DevCert);
        var keyAbs = Path.Combine(sslDir, DevKey);
        var caCert = Path.Combine(sslDir, CaCert);
        var caKey = Path.Combine(sslDir, CaKey);
        var csrPath = Path.Combine(sslDir, "dev.csr");
        var reqConfig = Path.Combine(sslDir, "openssl.cnf");
        var extConfig = Path.Combine(sslDir, "v3.ext");
        var caSerial = Path.Combine(sslDir, CaSerial);

        var san = domains
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static d => $"DNS:{d}")
            .Concat(["DNS:localhost", "IP:127.0.0.1"]);

        File.WriteAllText(reqConfig, """
[req]
distinguished_name = req_distinguished_name
prompt = no

[req_distinguished_name]
CN = Stackroot Dev
""", Encoding.UTF8);

        File.WriteAllText(extConfig, $$"""
[v3_req]
subjectAltName = {{string.Join(",", san)}}
""", Encoding.UTF8);

        if (RunProcess(openSsl, ["genrsa", "-out", keyAbs, "2048"]).ExitCode != 0)
        {
            return false;
        }

        if (RunProcess(openSsl, ["req", "-new", "-key", keyAbs, "-out", csrPath, "-config", reqConfig]).ExitCode != 0)
        {
            return false;
        }

        var signArgs = new List<string>
        {
            "x509",
            "-req",
            "-days",
            "825",
            "-sha256",
            "-in",
            csrPath,
            "-CA",
            caCert,
            "-CAkey",
            caKey,
            "-out",
            certAbs,
            "-extensions",
            "v3_req",
            "-extfile",
            extConfig
        };
        signArgs.AddRange(File.Exists(caSerial)
            ? ["-CAserial", caSerial]
            : ["-CAcreateserial"]);

        return RunProcess(openSsl, signArgs).ExitCode == 0 &&
               File.Exists(certAbs) &&
               File.Exists(keyAbs);
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
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

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
    }
}
