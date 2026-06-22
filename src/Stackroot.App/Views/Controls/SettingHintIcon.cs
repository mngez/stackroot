using System.Windows;
using System.Windows.Controls;

namespace Stackroot.App.Views.Controls;

public class SettingHintIcon : Control
{
    static SettingHintIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingHintIcon), new FrameworkPropertyMetadata(typeof(SettingHintIcon)));
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint),
        typeof(string),
        typeof(SettingHintIcon),
        new PropertyMetadata(string.Empty));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ToolTip = Hint;
        ToolTipService.SetShowDuration(this, 60_000);
        ToolTipService.SetInitialShowDelay(this, 200);
    }
}
