using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Media = System.Windows.Media;

namespace Stackroot.App.Helpers;

internal readonly record struct LogSegment(
    string Text,
    string? ForegroundHex,
    string? BackgroundHex,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false);

internal static class LogColorizer
{
    private const string DefaultHex = "#D4D4D4";
    private const string ErrorHex = "#F48787";
    private const string WarnHex = "#E9BD5B";
    private const string InfoHex = "#8FD6B6";
    private const string MutedHex = "#9DA5B4";

    private static readonly Dictionary<string, Media.Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool ContainsAnsi(string text) =>
        text.Contains('\u001b', StringComparison.Ordinal);

    public static IReadOnlyList<LogSegment> ParseSegments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [new LogSegment("(no output yet)", MutedHex, null)];
        }

        if (ContainsAnsi(text))
        {
            // VT replay needs original CR/LF bytes — LF-only breaks column tracking (Pest, PHPUnit, npm).
            return TerminalLogRenderer.ParseSegments(text);
        }

        return ParsePlainSegments(NormalizePlainText(text));
    }

    public static void ApplySegments(RichTextBox viewer, IReadOnlyList<LogSegment> segments, bool scrollToEnd = true)
    {
        viewer.Document ??= new FlowDocument();
        viewer.Document.Blocks.Clear();

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var segment in segments)
        {
            paragraph.Inlines.Add(CreateRun(segment));
        }

        viewer.Document.Blocks.Add(paragraph);
        if (scrollToEnd)
        {
            viewer.ScrollToEnd();
        }
    }

    public static void ApplySegmentsAppend(RichTextBox viewer, IReadOnlyList<LogSegment> segments, bool scrollToEnd = true)
    {
        viewer.Document ??= new FlowDocument();

        var paragraph = viewer.Document.Blocks.FirstBlock as Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph { Margin = new Thickness(0) };
            viewer.Document.Blocks.Add(paragraph);
        }

        foreach (var segment in segments)
        {
            paragraph.Inlines.Add(CreateRun(segment));
        }

        if (scrollToEnd)
        {
            viewer.ScrollToEnd();
        }
    }

    public static string TrimTailForDisplay(string content, int maxChars)
    {
        if (content.Length <= maxChars)
        {
            return content;
        }

        var tail = content[^maxChars..];
        var firstBreak = tail.IndexOf('\n');
        if (firstBreak >= 0)
        {
            tail = tail[(firstBreak + 1)..];
        }

        return tail;
    }

    public static void Apply(RichTextBox viewer, string? text, bool scrollToEnd = true) =>
        ApplySegments(viewer, ParseSegments(text), scrollToEnd);

    private static IReadOnlyList<LogSegment> ParsePlainSegments(string text) =>
        SplitDisplayLines(text)
            .Select(line => new LogSegment(
                string.IsNullOrEmpty(line) ? Environment.NewLine : line + Environment.NewLine,
                ResolveLineHex(line),
                null))
            .ToArray();

    private static string NormalizePlainText(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.Contains("[stderr]", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = StripLegacyStreamPrefix(lines[i]);
        }

        return string.Join('\n', lines);
    }

    private static Run CreateRun(LogSegment segment)
    {
        var run = new Run(segment.Text)
        {
            Foreground = BrushFromHex(segment.ForegroundHex ?? DefaultHex),
            Background = segment.BackgroundHex is null ? null : BrushFromHex(segment.BackgroundHex),
            FontWeight = segment.Bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStyle = segment.Italic ? FontStyles.Italic : FontStyles.Normal,
        };

        if (segment.Underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }

        return run;
    }

    private static IEnumerable<string> SplitDisplayLines(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var lines = text.Split('\n');
        var count = text.EndsWith('\n') && lines.Length > 0 && lines[^1] == string.Empty
            ? lines.Length - 1
            : lines.Length;

        for (var i = 0; i < count; i++)
        {
            yield return lines[i];
        }
    }

    private static string StripLegacyStreamPrefix(string line)
    {
        if (line.StartsWith("[stderr] ", StringComparison.OrdinalIgnoreCase))
        {
            return line["[stderr] ".Length..];
        }

        if (line.StartsWith("[err] ", StringComparison.OrdinalIgnoreCase))
        {
            return line["[err] ".Length..];
        }

        return line;
    }

    private static string ResolveLineHex(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return DefaultHex;
        }

        var normalized = line.TrimStart().ToLowerInvariant();
        if (normalized.Contains("error", StringComparison.Ordinal) ||
            normalized.Contains("fatal", StringComparison.Ordinal) ||
            normalized.Contains("exception", StringComparison.Ordinal))
        {
            return ErrorHex;
        }

        if (normalized.Contains("warn", StringComparison.Ordinal) ||
            normalized.Contains("warning", StringComparison.Ordinal))
        {
            return WarnHex;
        }

        if (normalized.StartsWith("[info]", StringComparison.Ordinal) ||
            normalized.Contains(" notice:", StringComparison.Ordinal))
        {
            return InfoHex;
        }

        return DefaultHex;
    }

    private static Media.Brush BrushFromHex(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        var brush = FreezeBrush(hex);
        BrushCache[hex] = brush;
        return brush;
    }

    private static Media.Brush FreezeBrush(string hex)
    {
        var color = ParseHexColor(hex);
        var brush = new Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Media.Color ParseHexColor(string hex)
    {
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        return Media.Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }
}
