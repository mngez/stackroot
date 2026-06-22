using Stackroot.Core.Windows;
using XTerm;
using XTerm.Buffer;
using XTerm.Options;

namespace Stackroot.App.Helpers;

internal static class TerminalLogRenderer
{
    private const int DefaultColumns = PseudoConsoleCapture.DefaultColumns;
    private const int DefaultRows = PseudoConsoleCapture.DefaultRows;
    private const int ScrollbackLines = 50_000;

    public static IReadOnlyList<LogSegment> ParseSegments(string text)
    {
        var terminal = CreateTerminal();
        terminal.Write(text);

        var results = new List<LogSegment>();
        var buffer = terminal.Buffer;
        var totalLines = buffer.Length;
        var startRow = 0;

        while (startRow < totalLines && IsBlankLine(buffer.Lines[startRow], terminal.Cols))
        {
            startRow++;
        }

        var endRow = totalLines - 1;
        while (endRow >= startRow && IsBlankLine(buffer.Lines[endRow], terminal.Cols))
        {
            endRow--;
        }

        for (var row = startRow; row <= endRow; row++)
        {
            var line = buffer.Lines[row];
            if (line is null)
            {
                results.Add(new LogSegment(Environment.NewLine, DefaultLogHex, null));
                continue;
            }

            AppendLineSegments(results, line, terminal.Cols);
        }

        return results;
    }

    private static Terminal CreateTerminal() =>
        new(new TerminalOptions
        {
            Cols = DefaultColumns,
            Rows = DefaultRows,
            Scrollback = ScrollbackLines,
            TermName = "xterm-256color",
        });

    private static bool IsBlankLine(BufferLine? line, int columns)
    {
        if (line is null)
        {
            return true;
        }

        for (var col = 0; col < columns; col++)
        {
            var cell = line[col];
            if (cell.Width == 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(cell.Content) && !IsEmptyCell(cell))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendLineSegments(List<LogSegment> results, BufferLine line, int columns)
    {
        var firstColumn = 0;
        while (firstColumn < columns && IsEmptyCell(line[firstColumn]))
        {
            firstColumn++;
        }

        var lastColumn = columns - 1;
        while (lastColumn >= firstColumn && IsEmptyCell(line[lastColumn]))
        {
            lastColumn--;
        }

        if (lastColumn < firstColumn)
        {
            results.Add(new LogSegment(Environment.NewLine, DefaultLogHex, null));
            return;
        }

        LogSegment? pending = null;
        for (var col = firstColumn; col <= lastColumn; col++)
        {
            var cell = line[col];
            if (cell.Width == 0)
            {
                continue;
            }

            var segment = CellToSegment(cell);
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (pending is { } previous && SegmentsShareStyle(previous, segment))
            {
                pending = previous with { Text = previous.Text + segment.Text };
            }
            else
            {
                if (pending is { } flush)
                {
                    results.Add(flush);
                }

                pending = segment;
            }
        }

        if (pending is { } tail)
        {
            results.Add(tail);
        }

        results.Add(new LogSegment(Environment.NewLine, DefaultLogHex, null));
    }

    private static bool IsEmptyCell(BufferCell cell) =>
        cell.Width == 0 || string.IsNullOrEmpty(cell.Content) || cell.Content is " ";

    private static LogSegment CellToSegment(BufferCell cell)
    {
        var text = cell.Content;
        if (string.IsNullOrEmpty(text))
        {
            return new LogSegment(string.Empty, DefaultLogHex, null);
        }

        var attributes = cell.Attributes;
        var foreground = ResolveForegroundHex(attributes) ?? DefaultLogHex;
        if (attributes.IsDim())
        {
            foreground = DimHex(foreground);
        }

        var background = ResolveBackgroundHex(attributes);

        if (attributes.IsInverse())
        {
            (foreground, background) = (background ?? DefaultLogHex, foreground);
        }

        return new LogSegment(
            text,
            foreground,
            background,
            attributes.IsBold(),
            attributes.IsItalic(),
            attributes.IsUnderline());
    }

    private static string? ColorToHex(int mode, int color)
    {
        // XTerm.NET default fg/bg sentinels (see AttributeData.Default).
        if (color is 256 or 257)
        {
            return null;
        }

        return mode switch
        {
            2 => RgbPackedToHex(color),
            1 => Palette256ToHex(color),
            0 when color is >= 0 and <= 255 => Palette256ToHex(color),
            _ => null,
        };
    }

    private static string? ResolveForegroundHex(AttributeData attributes)
    {
        var mode = attributes.GetFgColorMode();
        var color = attributes.GetFgColor();
        return ColorToHex(mode, color);
    }

    private static string? ResolveBackgroundHex(AttributeData attributes)
    {
        var mode = attributes.GetBgColorMode();
        var color = attributes.GetBgColor();
        return ColorToHex(mode, color);
    }

    private static string Palette256ToHex(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16)
        {
            // Windows Terminal palette — matches PHPUnit/Pest/Symfony output.
            return index switch
            {
                0 => "#0C0C0C",
                1 => "#E74856",
                2 => "#16C60C",
                3 => "#F9F1A5",
                4 => "#3B78FF",
                5 => "#B4009E",
                6 => "#61D6D6",
                7 => "#CCCCCC",
                8 => "#767676",
                9 => "#E74856",
                10 => "#16C60C",
                11 => "#F9F1A5",
                12 => "#3B78FF",
                13 => "#B4009E",
                14 => "#61D6D6",
                15 => "#F2F2F2",
                _ => "#D4D4D4",
            };
        }

        if (index < 232)
        {
            index -= 16;
            var r = index / 36;
            var g = (index / 6) % 6;
            var b = index % 6;
            return RgbToHex(r * 51, g * 51, b * 51);
        }

        var gray = (index - 232) * 10 + 8;
        return RgbToHex(gray, gray, gray);
    }

    private static string RgbPackedToHex(int packed) =>
        RgbToHex((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);

    private static string RgbToHex(int red, int green, int blue) =>
        $"#{Math.Clamp(red, 0, 255):X2}{Math.Clamp(green, 0, 255):X2}{Math.Clamp(blue, 0, 255):X2}";

    private static string DimHex(string hex)
    {
        if (!hex.StartsWith('#') || hex.Length != 7)
        {
            return hex;
        }

        static byte Channel(string slice) => Convert.ToByte(slice, 16);
        return RgbToHex(
            (int)(Channel(hex[1..3]) * 0.62),
            (int)(Channel(hex[3..5]) * 0.62),
            (int)(Channel(hex[5..7]) * 0.62));
    }

    private static bool SegmentsShareStyle(LogSegment left, LogSegment right) =>
        left.ForegroundHex == right.ForegroundHex
        && left.BackgroundHex == right.BackgroundHex
        && left.Bold == right.Bold
        && left.Italic == right.Italic
        && left.Underline == right.Underline;

    private const string DefaultLogHex = "#D4D4D4";
}
