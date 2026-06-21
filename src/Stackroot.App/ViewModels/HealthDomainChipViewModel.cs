using System.Windows.Media;

namespace Stackroot.App.ViewModels;

/// <summary>
/// Compact health indicator for one domain (web stack, user processes, scheduler).
/// </summary>
public sealed class HealthDomainChipViewModel : ViewModelBase
{
    private EnvironmentHealthLevel _level = EnvironmentHealthLevel.Healthy;
    private string _summary = string.Empty;
    private string _healthyBadgeText = "OK";
    private bool _isVisible;
    private string _badgeBackground = "#142019";
    private string _textColor = "#8FD6B6";
    private string _indicatorColor = "#4CAE8C";

    public string DomainName { get; init; } = string.Empty;

    public EnvironmentHealthLevel Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                RaisePropertyChanged(nameof(ToolTip));
                RaisePropertyChanged(nameof(BadgeText));
            }
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public string BadgeBackground
    {
        get => _badgeBackground;
        private set => SetProperty(ref _badgeBackground, value);
    }

    public string TextColor
    {
        get => _textColor;
        private set
        {
            if (SetProperty(ref _textColor, value))
            {
                RaisePropertyChanged(nameof(IndicatorBrush));
                RaisePropertyChanged(nameof(TextBrush));
            }
        }
    }

    public System.Windows.Media.Brush IndicatorBrush => CreateBrush(_indicatorColor);

    public System.Windows.Media.Brush TextBrush => CreateBrush(_textColor);

    public string BadgeText =>
        Level == EnvironmentHealthLevel.Healthy
            ? _healthyBadgeText
            : BuildShortLabel(Summary);

    public string ToolTip =>
        string.IsNullOrWhiteSpace(Summary)
            ? DomainName
            : $"{DomainName} — {Summary}";

    public void SetPresentation(
        EnvironmentHealthLevel level,
        string summary,
        string healthyBadgeText,
        bool visible = true)
    {
        Level = level;
        Summary = summary;
        _healthyBadgeText = healthyBadgeText;
        IsVisible = visible;
        ApplyColors(level);
        RaisePropertyChanged(nameof(BadgeText));
        RaisePropertyChanged(nameof(ToolTip));
    }

    private void ApplyColors(EnvironmentHealthLevel level)
    {
        switch (level)
        {
            case EnvironmentHealthLevel.Critical:
                BadgeBackground = "#2A1418";
                TextColor = "#EAAAB0";
                _indicatorColor = "#E88A92";
                break;
            case EnvironmentHealthLevel.Degraded:
                BadgeBackground = "#2A2414";
                TextColor = "#E9BD5B";
                _indicatorColor = "#E9BD5B";
                break;
            default:
                BadgeBackground = "#142019";
                TextColor = "#8FD6B6";
                _indicatorColor = "#4CAE8C";
                break;
        }

        RaisePropertyChanged(nameof(IndicatorBrush));
    }

    private static string BuildShortLabel(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "Needs attention";
        }

        var first = summary.Split(" · ", 2, StringSplitOptions.TrimEntries)[0];
        return first.Length <= 40 ? first : first[..37] + "…";
    }

    private static System.Windows.Media.SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}
