using System.Windows;
using System.Windows.Controls;
using Stackroot.App.Services;
using Stackroot.App.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Stackroot.App.Views;

public partial class BackgroundAlertDialog : Window
{
    public BackgroundAlertDialog(BackgroundAlert alert)
    {
        InitializeComponent();
        WindowsTheme.HookDarkTitleBar(this);
        ApplyAlert(alert);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }

    public static void Show(Window? owner, BackgroundAlert alert)
    {
        var dialog = new BackgroundAlertDialog(alert)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
    }

    private void ApplyAlert(BackgroundAlert alert)
    {
        TitleText.Text = alert.Title;
        MessageText.Text = alert.Message;

        var detail = alert.Detail ?? alert.Exception?.Message;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailBorder.Visibility = Visibility.Visible;
        }

        ApplyKindStyling(alert.Kind);
        BuildButtons(alert.Actions);
    }

    private void ApplyKindStyling(BackgroundAlertKind kind)
    {
        var (glyph, fgKey, bgHex, borderHex) = kind switch
        {
            BackgroundAlertKind.Error   => ("\uE783", "StackrootDangerTextBrush", "#1AE88A92", "#52E88A92"),
            BackgroundAlertKind.Warning => ("\uE7BA", "StackrootWarnBrush",       "#1AE9BD5B", "#52E9BD5B"),
            BackgroundAlertKind.Success => ("\uE73E", "StackrootAccentTextBrush", "#1A4CAE8C", "#524CAE8C"),
            _                           => ("\uE946", "StackrootAccentTextBrush", "#1A4CAE8C", "#524CAE8C"),
        };

        KindIcon.Text = glyph;
        KindIcon.Foreground = (WpfBrush)FindResource(fgKey);
        IconBorder.Background = new WpfSolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(bgHex));
        IconBorder.BorderBrush = new WpfSolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(borderHex));
    }

    private void BuildButtons(IReadOnlyList<BackgroundAlertAction>? actions)
    {
        if (actions is not null)
        {
            foreach (var action in actions)
            {
                var btn = new Button
                {
                    Content = action.Label,
                    MinWidth = 100,
                    Margin = new Thickness(0, 0, 8, 0),
                    Style = (Style)FindResource("StackrootButtonStyle")
                };
                btn.Click += (_, _) => { action.Execute(); Close(); };
                ButtonsPanel.Children.Add(btn);
            }
        }

        var closeText = TryFindResource("Loc.Common.Close") as string ?? "Close";
        var close = new Button
        {
            Content = closeText,
            MinWidth = 80,
            Style = (Style)FindResource("StackrootPrimaryButtonStyle")
        };
        close.Click += (_, _) => Close();
        ButtonsPanel.Children.Add(close);
    }
}
