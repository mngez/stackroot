using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Stackroot.Core.Nginx;

internal static class DevSslDotNetCertificateGenerator
{
    private const string CaName = "Stackroot Local CA";

    public static bool TryGenerate(string sslDir, IReadOnlyList<string> domains)
    {
        try
        {
            Directory.CreateDirectory(sslDir);

            var caCertPath = Path.Combine(sslDir, "stackroot-ca.crt");
            var caKeyPath = Path.Combine(sslDir, "stackroot-ca.key");
            var devCertPath = Path.Combine(sslDir, "dev.crt");
            var devKeyPath = Path.Combine(sslDir, "dev.key");

            using var caKey = RSA.Create(4096);
            var caRequest = CreateCaRequest(caKey);

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var caNotAfter = DateTimeOffset.UtcNow.AddYears(20);
            using var caCertificate = caRequest.CreateSelfSigned(notBefore, caNotAfter);

            using var serverKey = RSA.Create(2048);
            using var serverCertificate = CreateServerCertificate(
                serverKey,
                caCertificate,
                domains,
                notBefore,
                DateTimeOffset.UtcNow.AddYears(2));

            WritePem(caCertPath, caCertificate.ExportCertificatePem());
            WritePem(caKeyPath, caKey.ExportRSAPrivateKeyPem());
            WritePem(devCertPath, serverCertificate.ExportCertificatePem());
            WritePem(devKeyPath, serverKey.ExportRSAPrivateKeyPem());

            return AllMaterialExists(sslDir);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-signs dev.crt/dev.key with the existing local CA (avoids re-trusting a new CA).
    /// </summary>
    public static bool TryRenewServerCertificate(string sslDir, IReadOnlyList<string> domains)
    {
        try
        {
            Directory.CreateDirectory(sslDir);

            var caCertPath = Path.Combine(sslDir, "stackroot-ca.crt");
            var caKeyPath = Path.Combine(sslDir, "stackroot-ca.key");
            var devCertPath = Path.Combine(sslDir, "dev.crt");
            var devKeyPath = Path.Combine(sslDir, "dev.key");

            if (!File.Exists(caCertPath) || !File.Exists(caKeyPath))
            {
                return false;
            }

            using var caCertificate = X509Certificate2.CreateFromPemFile(caCertPath, caKeyPath);
            if (!caCertificate.HasPrivateKey)
            {
                return false;
            }

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            using var serverKey = RSA.Create(2048);
            using var serverCertificate = CreateServerCertificate(
                serverKey,
                caCertificate,
                domains,
                notBefore,
                DateTimeOffset.UtcNow.AddYears(2));

            WritePem(devCertPath, serverCertificate.ExportCertificatePem());
            WritePem(devKeyPath, serverKey.ExportRSAPrivateKeyPem());

            return File.Exists(devCertPath) && File.Exists(devKeyPath);
        }
        catch
        {
            return false;
        }
    }

    private static CertificateRequest CreateCaRequest(RSA caKey)
    {
        var caRequest = new CertificateRequest(
            $"CN={CaName}, O=Stackroot, OU=Local Development",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return caRequest;
    }

    private static X509Certificate2 CreateServerCertificate(
        RSA serverKey,
        X509Certificate2 issuer,
        IReadOnlyList<string> domains,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        var serverRequest = new CertificateRequest(
            "CN=Stackroot Dev, O=Stackroot",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(domain.Trim().ToLowerInvariant());
        }

        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));
        serverRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        return serverRequest.Create(
            issuer,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16));
    }

    private static bool AllMaterialExists(string sslDir) =>
        File.Exists(Path.Combine(sslDir, "dev.crt"))
        && File.Exists(Path.Combine(sslDir, "dev.key"))
        && File.Exists(Path.Combine(sslDir, "stackroot-ca.crt"))
        && File.Exists(Path.Combine(sslDir, "stackroot-ca.key"));

    private static void WritePem(string path, string pem)
    {
        File.WriteAllText(path, pem, Encoding.ASCII);
    }
}
