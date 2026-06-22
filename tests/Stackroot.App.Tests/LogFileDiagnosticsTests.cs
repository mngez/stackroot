using System.IO;
using Stackroot.App.Helpers;
using Xunit;

namespace Stackroot.App.Tests;

public sealed class LogFileDiagnosticsTests
{
    private const string LatestLogPath =
        @"C:\Users\omarf\AppData\Roaming\Stackroot\logs\sites\zohuor-test\custom-a0c275f8-2026-06-22T07-28-17-015Z.log";

    [Fact]
    public void LatestSiteLog_PassBadgesAreBlackOnGreen()
    {
        if (!File.Exists(LatestLogPath))
        {
            return;
        }

        var segments = LogColorizer.ParseSegments(File.ReadAllText(LatestLogPath));
        var passBadges = segments
            .Where(static s => s.Text.Trim().Equals("PASS", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(passBadges);
        Assert.All(passBadges, static segment =>
        {
            Assert.Equal("#0C0C0C", segment.ForegroundHex, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("#16C60C", segment.BackgroundHex, StringComparer.OrdinalIgnoreCase);
            Assert.True(segment.Bold);
        });
    }
}
