using System.Text;
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
    private const string MutedHex = "#91A0B5";

    private static readonly Dictionary<int, string> ForegroundCodes = new()
    {
        [30] = "#ABB2BF",
        [31] = "#E06C75",
        [32] = "#98C379",
        [33] = "#E5C07B",
        [34] = "#61AFEF",
        [35] = "#C678DD",
        [36] = "#56B6C2",
        [37] = "#E6E6E6",
        [90] = "#5C6370",
        [91] = "#BE5046",
        [92] = "#7BBF88",
        [93] = "#D19A66",
        [94] = "#4B82C3",
        [95] = "#A855F7",
        [96] = "#4AA8A8",
        [97] = "#FFFFFF",
    };

    private static readonly Dictionary<int, string> BackgroundCodes = new()
    {
        [40] = "#282C34",
        [41] = "#E06C75",
        [42] = "#98C379",
        [43] = "#E5C07B",
        [44] = "#3178C6",
        [45] = "#C678DD",
        [46] = "#56B6C2",
        [47] = "#E6E6E6",
        [100] = "#3E4451",
        [101] = "#BE5046",
        [102] = "#7BBF88",
        [103] = "#D19A66",
        [104] = "#4B82C3",
        [105] = "#A855F7",
        [106] = "#4AA8A8",
        [107] = "#FFFFFF",
    };

    private static readonly Dictionary<string, Media.Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<LogSegment> ParseSegments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [new LogSegment("(no output yet)", MutedHex, null)];
        }

        if (ContainsAnsi(text))
        {
            return ParseAnsiSegments(NormalizeTerminalText(text));
        }

        return NormalizeTerminalText(text)
            .Split('\n')
            .Select(line => new LogSegment(
                string.IsNullOrEmpty(line) ? Environment.NewLine : line + Environment.NewLine,
                ResolveLineHex(line),
                null))
            .ToArray();
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

    private static IReadOnlyList<LogSegment> ParseAnsiSegments(string text)
    {
        var results = new List<LogSegment>();
        var style = new AnsiStyleState();
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }

            var chunk = plain.ToString();
            plain.Clear();

            if (style.HasExplicitStyle)
            {
                results.Add(CreateSegment(chunk, style));
                return;
            }

            foreach (var line in chunk.Split('\n'))
            {
                results.Add(new LogSegment(
                    string.IsNullOrEmpty(line) ? Environment.NewLine : line + Environment.NewLine,
                    ResolveLineHex(line),
                    null));
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\u001b')
            {
                plain.Append(ch);
                continue;
            }

            FlushPlain();

            if (i + 1 < text.Length && text[i + 1] == ']')
            {
                var endBell = text.IndexOf('\u0007', i + 2);
                var st = text.IndexOf("\u001b\\", i + 2, StringComparison.Ordinal);
                if (endBell != -1 && (st == -1 || endBell < st))
                {
                    i = endBell;
                    continue;
                }

                if (st != -1)
                {
                    i = st + 1;
                    continue;
                }

                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '[')
            {
                var j = i + 2;
                while (j < text.Length && !char.IsLetter(text[j]))
                {
                    j++;
                }

                if (j >= text.Length)
                {
                    break;
                }

                var letter = text[j];
                var parameters = text[(i + 2)..j];
                i = j;

                if (letter == 'm')
                {
                    ApplySgr(parameters, style);
                }

                continue;
            }

            plain.Append(ch);
        }

        FlushPlain();
        return results;
    }

    private static LogSegment CreateSegment(string text, AnsiStyleState style) =>
        new(
            text,
            style.ForegroundHex ?? DefaultHex,
            style.BackgroundHex,
            style.Bold,
            style.Italic,
            style.Underline);

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

    private static void ApplySgr(string parameters, AnsiStyleState style)
    {
        var codes = string.IsNullOrEmpty(parameters)
            ? new[] { 0 }
            : parameters.Split(';')
                .Select(part => string.IsNullOrEmpty(part) ? 0 : int.TryParse(part, out var code) ? code : -1)
                .Where(code => code >= 0)
                .ToArray();

        if (codes.Length == 0)
        {
            codes = new[] { 0 };
        }

        foreach (var code in codes)
        {
            switch (code)
            {
                case 0:
                    style.Reset();
                    break;
                case 1:
                    style.Bold = true;
                    break;
                case 2:
                    style.Dim = true;
                    break;
                case 3:
                    style.Italic = true;
                    break;
                case 4:
                    style.Underline = true;
                    break;
                case 22:
                    style.Bold = false;
                    break;
                case 23:
                    style.Italic = false;
                    break;
                case 24:
                    style.Underline = false;
                    break;
                case 39:
                    style.ForegroundHex = null;
                    break;
                case 49:
                    style.BackgroundHex = null;
                    break;
                default:
                    if (ForegroundCodes.TryGetValue(code, out var fg))
                    {
                        style.ForegroundHex = fg;
                    }
                    else if (BackgroundCodes.TryGetValue(code, out var bg))
                    {
                        style.BackgroundHex = bg;
                    }

                    break;
            }
        }
    }

    private static string NormalizeTerminalText(string raw) =>
        raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool ContainsAnsi(string text) => text.Contains('\u001b', StringComparison.Ordinal);

    private static string ResolveLineHex(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return DefaultHex;
        }

        var normalized = line.TrimStart().ToLowerInvariant();
        if (normalized.StartsWith("[err]", StringComparison.Ordinal) ||
            normalized.StartsWith("[stderr]", StringComparison.Ordinal) ||
            normalized.Contains("error", StringComparison.Ordinal) ||
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
        var color = (Media.Color)Media.ColorConverter.ConvertFromString(hex)!;
        var brush = new Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class AnsiStyleState
    {
        public string? ForegroundHex { get; set; }
        public string? BackgroundHex { get; set; }
        public bool Bold { get; set; }
        public bool Dim { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }

        public bool HasExplicitStyle =>
            ForegroundHex is not null ||
            BackgroundHex is not null ||
            Bold ||
            Dim ||
            Italic ||
            Underline;

        public void Reset()
        {
            ForegroundHex = null;
            BackgroundHex = null;
            Bold = false;
            Dim = false;
            Italic = false;
            Underline = false;
        }
    }
}
