using Stackroot.App.Helpers;
using Xunit;

namespace Stackroot.App.Tests;

public sealed class LogColorizerTests
{
    [Fact]
    public void NpmAuditSeverityColorsStayOnOneLine()
    {
        var ansi =
            "45 vulnerabilities (7 \u001b[33mlow\u001b[39m, 20 \u001b[33mmoderate\u001b[39m, 16 \u001b[33mhigh\u001b[39m, 2 \u001b[31mcritical\u001b[39m)\n";

        var flat = Flatten(LogColorizer.ParseSegments(ansi));

        Assert.Contains("7 low, 20 moderate, 16 high, 2 critical", flat);
        Assert.DoesNotContain("7 \nlow", flat);
        Assert.DoesNotContain("20 \nmoderate", flat);
    }

    [Fact]
    public void NpmAuditSuggestionBlockPreservesLines()
    {
        var ansi =
            "\nTo address issues that do not require attention, run:\n  \u001b[36mnpm audit fix\u001b[39m\n\nSome issues need review, and may require choosing\na different dependency.\n\nRun `npm audit` for details.\n";

        var flat = Flatten(LogColorizer.ParseSegments(ansi));

        Assert.Contains("To address issues that do not require attention, run:", flat);
        Assert.Contains("npm audit fix", flat);
        Assert.Contains("Some issues need review, and may require choosing", flat);
        Assert.Contains("Run `npm audit` for details.", flat);
    }

    [Fact]
    public void PlainTextStillHighlightsErrors()
    {
        var segments = LogColorizer.ParseSegments("Fatal error: something failed\n");

        Assert.Contains(segments, static segment =>
            segment.Text.Contains("Fatal error", StringComparison.Ordinal)
            && segment.ForegroundHex == "#F48787");
    }

    [Fact]
    public void PestCursorReplaySeparatesTestResultFromNextSuiteHeader()
    {
        var ansi =
            "  \u001b[32m\u001b[1m✓\u001b[90m\u001b[22m cast value handles all types correctly                                                                       0.14s  \u001b[m\u001b[14;1H  \u001b[30m\u001b[42m\u001b[1m PASS \u001b[m Tests\\Unit\\StaffCanAttendOutsideTest\n";

        var flat = Flatten(LogColorizer.ParseSegments(ansi));

        Assert.Contains("cast value handles all types correctly", flat, StringComparison.Ordinal);
        Assert.Contains("StaffCanAttendOutsideTest", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("0.14s  PASS", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("[14;1H", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void PestCursorReplayDoesNotGlueDurationToPassHeader()
    {
        var ansi =
            "  \u001b[32m\u001b[1m✓\u001b[90m\u001b[22m en number to ar converts to arabic numerals                                                                  0.13s  \u001b[m\u001b[11;1H  \u001b[30m\u001b[42m\u001b[1m PASS \u001b[m Tests\\Unit\\NovaSettingsUnitTest\n";

        var flat = Flatten(LogColorizer.ParseSegments(ansi));

        Assert.DoesNotContain("0.13s  PASS", flat, StringComparison.Ordinal);
        Assert.Contains("NovaSettingsUnitTest", flat, StringComparison.Ordinal);
    }

    private static string Flatten(IReadOnlyList<LogSegment> segments) =>
        string.Concat(segments.Select(static segment => segment.Text))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
