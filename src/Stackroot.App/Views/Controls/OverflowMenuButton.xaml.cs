using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Stackroot.App.Views.Controls;

public partial class OverflowMenuButton : UserControl
{
    public static readonly DependencyProperty DangerCommandProperty =
        DependencyProperty.Register(
            nameof(DangerCommand),
            typeof(ICommand),
            typeof(OverflowMenuButton),
            new PropertyMetadata(null, OnDangerActionChanged));

    public static readonly DependencyProperty DangerCommandParameterProperty =
        DependencyProperty.Register(
            nameof(DangerCommandParameter),
            typeof(object),
            typeof(OverflowMenuButton),
            new PropertyMetadata(null, OnDangerActionChanged));

    public static readonly DependencyProperty DangerLabelProperty =
        DependencyProperty.Register(
            nameof(DangerLabel),
            typeof(string),
            typeof(OverflowMenuButton),
            new PropertyMetadata("Delete", OnDangerActionChanged));

    public static readonly DependencyProperty ButtonWidthProperty =
        DependencyProperty.Register(
            nameof(ButtonWidth),
            typeof(double),
            typeof(OverflowMenuButton),
            new PropertyMetadata(32d));

    public static readonly DependencyProperty ButtonHeightProperty =
        DependencyProperty.Register(
            nameof(ButtonHeight),
            typeof(double),
            typeof(OverflowMenuButton),
            new PropertyMetadata(32d));

    public static readonly DependencyProperty ToolTipTextProperty =
        DependencyProperty.Register(
            nameof(ToolTipText),
            typeof(string),
            typeof(OverflowMenuButton),
            new PropertyMetadata("More actions"));

    public OverflowMenuButton()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshState();
        IsVisibleChanged += (_, _) => RefreshState();
    }

    public ICommand? DangerCommand
    {
        get => (ICommand?)GetValue(DangerCommandProperty);
        set => SetValue(DangerCommandProperty, value);
    }

    public object? DangerCommandParameter
    {
        get => GetValue(DangerCommandParameterProperty);
        set => SetValue(DangerCommandParameterProperty, value);
    }

    public string DangerLabel
    {
        get => (string)GetValue(DangerLabelProperty);
        set => SetValue(DangerLabelProperty, value);
    }

    public double ButtonWidth
    {
        get => (double)GetValue(ButtonWidthProperty);
        set => SetValue(ButtonWidthProperty, value);
    }

    public double ButtonHeight
    {
        get => (double)GetValue(ButtonHeightProperty);
        set => SetValue(ButtonHeightProperty, value);
    }

    public string ToolTipText
    {
        get => (string)GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    private static void OnDangerActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OverflowMenuButton button)
        {
            return;
        }

        if (e.Property == DangerCommandProperty)
        {
            if (e.OldValue is ICommand oldCommand)
            {
                oldCommand.CanExecuteChanged -= button.OnDangerCanExecuteChanged;
            }

            if (e.NewValue is ICommand newCommand)
            {
                newCommand.CanExecuteChanged += button.OnDangerCanExecuteChanged;
            }
        }

        button.RefreshState();
    }

    private void OnDangerCanExecuteChanged(object? sender, EventArgs e) => RefreshState();

    private void RefreshState()
    {
        var canRun = DangerCommand is not null && DangerCommand.CanExecute(DangerCommandParameter);
        Visibility = DangerCommand is null ? Visibility.Collapsed : Visibility.Visible;
        TriggerButton.IsEnabled = canRun;
    }

    private void OnOpenMenuClick(object sender, RoutedEventArgs e)
    {
        if (DangerCommand is null || !DangerCommand.CanExecute(DangerCommandParameter))
        {
            return;
        }

        if (TriggerButton.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        DangerActionButton.Content = DangerLabel;
        DangerActionButton.IsEnabled = true;
        menu.Tag = this;
        menu.PlacementTarget = TriggerButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnDangerMenuClick(object sender, RoutedEventArgs e)
    {
        if (TriggerButton.ContextMenu?.Tag is OverflowMenuButton owner
            && owner.DangerCommand?.CanExecute(owner.DangerCommandParameter) == true)
        {
            owner.DangerCommand.Execute(owner.DangerCommandParameter);
        }

        if (TriggerButton.ContextMenu is { } menu)
        {
            menu.IsOpen = false;
        }

        e.Handled = true;
    }
}
