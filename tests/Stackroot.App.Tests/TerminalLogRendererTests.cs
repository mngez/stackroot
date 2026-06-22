using Stackroot.App.Helpers;
using XTerm;
using XTerm.Options;
using Xunit;

namespace Stackroot.App.Tests;

public sealed class TerminalLogRendererTests
{
    [Fact]
    public void PreservesAnsiForegroundColors()
    {
        var segments = LogColorizer.ParseSegments("\u001b[32mgreen\u001b[0m\r\n");

        Assert.Contains(segments, static segment =>
            segment.Text.Contains("green", StringComparison.Ordinal)
            && segment.ForegroundHex is not null
            && string.Equals(segment.ForegroundHex, "#16C60C", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CrlfKeepsPassHeaderOnItsOwnLine()
    {
        var ansi =
            "  \u001b[32m\u001b[1m✓\u001b[90m\u001b[22m cast value handles all types correctly                                                                       0.14s  \u001b[m\r\n  \u001b[30m\u001b[42m\u001b[1m PASS \u001b[m Tests\\Unit\\StaffCanAttendOutsideTest\r\n";

        var flat = Flatten(LogColorizer.ParseSegments(ansi));

        Assert.Contains("StaffCanAttendOutsideTest", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("0.14s  PASS", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void CursorPositionPlacesPassOnTargetRow()
    {
        var ansi =
            "  \u001b[32m\u001b[1m✓\u001b[90m\u001b[22m cast value handles all types correctly                                                                       0.14s  \u001b[m\u001b[14;1H  \u001b[30m\u001b[42m\u001b[1m PASS \u001b[m Tests\\Unit\\StaffCanAttendOutsideTest\r\n";

        var flat = Flatten(LogColorizer.ParseSegments(ansi));

        Assert.Contains("cast value handles all types correctly", flat, StringComparison.Ordinal);
        Assert.Contains("StaffCanAttendOutsideTest", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("0.14s  PASS", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void ColoredBadgePreservesInteriorPaddingSpaces()
    {
        var ansi = "\u001b[30m\u001b[42m\u001b[1m XY \u001b[m\r\n";

        var segments = LogColorizer.ParseSegments(ansi);

        Assert.Contains(segments, static segment =>
            string.Equals(segment.Text, " XY ", StringComparison.Ordinal)
            && string.Equals(segment.ForegroundHex, "#0C0C0C", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segment.BackgroundHex, "#16C60C", StringComparison.OrdinalIgnoreCase)
            && segment.Bold);
    }

    [Fact]
    public void BoldOnlyBadgePreservesPaddingSpaces()
    {
        var ansi = "\u001b[1m AB \u001b[m\r\n";

        var segments = LogColorizer.ParseSegments(ansi);

        Assert.Contains(segments, static segment =>
            string.Equals(segment.Text, " AB ", StringComparison.Ordinal) && segment.Bold);
    }

    [Fact]
    public void TerminalOptionsExposeConvertEol()
    {
        var property = typeof(TerminalOptions).GetProperty("ConvertEol");
        Assert.NotNull(property);
    }

    private static string Flatten(IReadOnlyList<LogSegment> segments) =>
        string.Concat(segments.Select(static segment => segment.Text))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
