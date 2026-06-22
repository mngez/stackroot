using System.Windows;
using System.Windows.Controls;

namespace Stackroot.App.Views.Controls;

public class SettingHintIcon : Control
{
    private const double ToolTipMaxWidth = 360;

    static SettingHintIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingHintIcon), new FrameworkPropertyMetadata(typeof(SettingHintIcon)));
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint),
        typeof(string),
        typeof(SettingHintIcon),
        new PropertyMetadata(string.Empty, OnHintChanged));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ToolTipService.SetShowDuration(this, 60_000);
        ToolTipService.SetInitialShowDelay(this, 200);
        UpdateToolTip();
    }

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingHintIcon icon)
        {
            icon.UpdateToolTip();
        }
    }

    private void UpdateToolTip()
    {
        if (string.IsNullOrWhiteSpace(Hint))
        {
            ToolTip = null;
            return;
        }

        ToolTip = new ToolTip
        {
            MaxWidth = ToolTipMaxWidth,
            Padding = new Thickness(10, 8, 10, 8),
            Content = new TextBlock
            {
                Text = Hint,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            }
        };
    }
}
