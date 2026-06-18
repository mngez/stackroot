using System.Text.RegularExpressions;

namespace Stackroot.Core.Windows;

public static partial class PhpRuntimeAliases
{
    [GeneratedRegex(@"^php-(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PhpPackageIdRegex();

    public static string? AliasForPackageId(string packageId)
    {
        var match = PhpPackageIdRegex().Match(packageId);
        if (!match.Success)
        {
            return null;
        }

        return $"php{match.Groups[1].Value}{match.Groups[2].Value}";
    }
}
