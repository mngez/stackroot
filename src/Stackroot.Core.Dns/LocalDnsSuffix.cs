using System.Text.RegularExpressions;

namespace Stackroot.Core.Dns;

public static partial class LocalDnsSuffix
{
  public const string CatchAllSuffix = ".";

  private static readonly HashSet<string> SafeSuffixes = new(StringComparer.OrdinalIgnoreCase)
  {
    ".test",
    ".localhost",
    ".invalid",
    ".example"
  };

  [GeneratedRegex(@"^\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
  private static partial Regex SuffixPattern();

  public static bool IsCatchAllSuffix(string? normalizedSuffix) =>
    string.Equals(normalizedSuffix, CatchAllSuffix, StringComparison.Ordinal);

  public static bool IsSafeSuffix(string normalizedSuffix) =>
    SafeSuffixes.Contains(normalizedSuffix);

  public static bool ContainsCatchAll(IEnumerable<string>? suffixes) =>
    suffixes?.Any(IsCatchAllSuffix) == true;

  public static bool TextContainsCatchAll(string? text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return false;
    }

    foreach (var line in text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      if (line.Trim() == CatchAllSuffix)
      {
        return true;
      }
    }

    return false;
  }

  public static bool TextContainsCatchAll(string? text, bool allowDangerous)
    => allowDangerous && TextContainsCatchAll(text);

  public static string? TryNormalize(string? value, bool allowDangerous = false)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var trimmed = value.Trim().ToLowerInvariant();
    if (allowDangerous && trimmed == CatchAllSuffix)
    {
      return CatchAllSuffix;
    }

    if (!trimmed.StartsWith('.'))
    {
      trimmed = "." + trimmed;
    }

    return SuffixPattern().IsMatch(trimmed) ? trimmed : null;
  }

  public static List<string> NormalizeList(
    IEnumerable<string>? values,
    bool ensureDefaultTest = true,
    bool allowDangerous = false)
  {
    var output = new List<string>();
    foreach (var value in values ?? [])
    {
      var normalized = TryNormalize(value, allowDangerous);
      if (normalized is null || output.Contains(normalized, StringComparer.OrdinalIgnoreCase))
      {
        continue;
      }

      output.Add(normalized);
    }

    if (output.Count == 0 && ensureDefaultTest)
    {
      output.Add(".test");
    }

    return output;
  }

  public static List<string> ParseText(string? text, bool allowDangerous = false) =>
    NormalizeList(
      string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
      ensureDefaultTest: !allowDangerous || !TextContainsCatchAll(text, allowDangerous: true),
      allowDangerous: allowDangerous);

  public static string FormatText(IEnumerable<string>? suffixes)
  {
    var allowDangerous = ContainsCatchAll(suffixes);
    return string.Join(Environment.NewLine, NormalizeList(suffixes, ensureDefaultTest: false, allowDangerous: allowDangerous));
  }

  public static string? ValidateText(string? text, bool allowDangerous = false)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return "Add at least one suffix (for example .test).";
    }

    var lines = text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (lines.Length == 0)
    {
      return "Add at least one suffix (for example .test).";
    }

    foreach (var line in lines)
    {
      if (TryNormalize(line, allowDangerous) is null)
      {
        if (!allowDangerous && line.Trim() == CatchAllSuffix)
        {
          return "Catch-all suffix \".\" is blocked. Enable testDns.allowDangerousSettings in settings.json first.";
        }

        return $"Invalid suffix: {line}";
      }
    }

    if (!allowDangerous && TextContainsCatchAll(text))
    {
      return "Catch-all suffix \".\" is blocked. Enable testDns.allowDangerousSettings in settings.json first.";
    }

    return null;
  }

  public static bool EndsWithSuffix(string hostName, string normalizedSuffix)
  {
    if (IsCatchAllSuffix(normalizedSuffix))
    {
      return !string.IsNullOrWhiteSpace(hostName.Trim().TrimEnd('.'));
    }

    var normalized = hostName.Trim().TrimEnd('.').ToLowerInvariant();
    var suffix = normalizedSuffix.Trim().ToLowerInvariant();
    if (!suffix.StartsWith('.'))
    {
      suffix = "." + suffix;
    }

    return normalized.EndsWith(suffix, StringComparison.Ordinal)
           && normalized.Length > suffix.Length;
  }
}
