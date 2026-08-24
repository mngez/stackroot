using Stackroot.Core.Services.Php;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class PhpCaBundleTests
{
    [Fact]
    public void TryResolve_returns_null_when_missing()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.Null(PhpCaBundle.TryResolve(root));
            Assert.Null(PhpCaBundle.TryResolve(""));
            Assert.Null(PhpCaBundle.TryResolve(null!));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryResolve_returns_path_when_bundle_present()
    {
        var root = CreateTempDirectory();
        try
        {
            var packages = Path.Combine(root, "packages");
            Directory.CreateDirectory(packages);
            var path = Path.Combine(packages, PhpCaBundle.FileName);
            File.WriteAllText(path, new string('x', 2048));

            var resolved = PhpCaBundle.TryResolve(root);
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
            Assert.Equal(Path.GetFullPath(path), resolved, ignoreCase: true);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ApplyPhpIniPatches_sets_curl_and_openssl_ca_directives()
    {
        var root = CreateTempDirectory();
        try
        {
            var ca = Path.Combine(root, "packages", "cacert.pem");
            Directory.CreateDirectory(Path.GetDirectoryName(ca)!);
            File.WriteAllText(ca, new string('x', 2048));
            var caIni = PhpCaBundle.ToIniValue(ca);

            var patched = PhpIniMerge.ApplyPhpIniPatches(
                """
                ;curl.cainfo =
                ;openssl.cafile=
                memory_limit = 128M
                """,
                new PhpIniPatchOptions(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["curl.cainfo"] = caIni,
                        ["openssl.cafile"] = caIni
                    },
                    Array.Empty<string>(),
                    Array.Empty<string>()));

            Assert.Contains($"curl.cainfo = {caIni}", patched, StringComparison.Ordinal);
            Assert.Contains($"openssl.cafile = {caIni}", patched, StringComparison.Ordinal);
            Assert.DoesNotContain(";curl.cainfo", patched, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ToIniValue_uses_forward_slashes_and_quotes()
    {
        var value = PhpCaBundle.ToIniValue(@"C:\Users\demo\AppData\Roaming\Stackroot\resources\packages\cacert.pem");
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
        Assert.Contains("C:/Users/demo/AppData/Roaming/Stackroot/resources/packages/cacert.pem", value);
        Assert.DoesNotContain('\\', value.Trim('"'));
    }

    private static string CreateTempDirectory()
        => Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
