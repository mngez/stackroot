using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Stackroot.App.Views.Controls;

public partial class OverflowMenuButton : UserControl
{
    public static readonly DependencyProperty SecondaryCommandProperty =
        DependencyProperty.Register(
            nameof(SecondaryCommand),
            typeof(ICommand),
            typeof(OverflowMenuButton),
            new PropertyMetadata(null, OnSecondaryCommandChanged));

    public static readonly DependencyProperty SecondaryLabelProperty =
        DependencyProperty.Register(
            nameof(SecondaryLabel),
            typeof(string),
            typeof(OverflowMenuButton),
            new PropertyMetadata(string.Empty, OnSecondaryActionChanged));

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

    public ICommand? SecondaryCommand
    {
        get => (ICommand?)GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    public string SecondaryLabel
    {
        get => (string)GetValue(SecondaryLabelProperty);
        set => SetValue(SecondaryLabelProperty, value);
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

    private static void OnSecondaryCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OverflowMenuButton button) return;

        if (e.OldValue is ICommand oldCommand)
            oldCommand.CanExecuteChanged -= button.OnSecondaryCanExecuteChanged;
        if (e.NewValue is ICommand newCommand)
            newCommand.CanExecuteChanged += button.OnSecondaryCanExecuteChanged;

        button.RefreshState();
    }

    private static void OnSecondaryActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverflowMenuButton button) button.RefreshState();
    }

    private static void OnDangerActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OverflowMenuButton button) return;

        if (e.Property == DangerCommandProperty)
        {
            if (e.OldValue is ICommand oldCommand)
                oldCommand.CanExecuteChanged -= button.OnDangerCanExecuteChanged;
            if (e.NewValue is ICommand newCommand)
                newCommand.CanExecuteChanged += button.OnDangerCanExecuteChanged;
        }

        button.RefreshState();
    }

    private void OnSecondaryCanExecuteChanged(object? sender, EventArgs e) => RefreshState();
    private void OnDangerCanExecuteChanged(object? sender, EventArgs e) => RefreshState();

    private void RefreshState()
    {
        var hasSecondary = SecondaryCommand is not null;
        var hasDanger = DangerCommand is not null;
        var secondaryCanExecute = SecondaryCommand?.CanExecute(null) == true;
        var dangerCanExecute = DangerCommand?.CanExecute(DangerCommandParameter) == true;
        Visibility = (hasSecondary || hasDanger) ? Visibility.Visible : Visibility.Collapsed;
        TriggerButton.IsEnabled = secondaryCanExecute || dangerCanExecute;

        if (SecondaryActionButton is not null)
        {
            SecondaryActionButton.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
            SecondaryActionButton.IsEnabled = secondaryCanExecute;
        }
        if (DangerActionButton is not null)
        {
            DangerActionButton.Visibility = hasDanger ? Visibility.Visible : Visibility.Collapsed;
            DangerActionButton.IsEnabled = dangerCanExecute;
        }
    }

    private void OnOpenMenuClick(object sender, RoutedEventArgs e)
    {
        if (TriggerButton.ContextMenu is not ContextMenu menu) return;

        if (SecondaryActionButton is not null)
        {
            SecondaryActionButton.Content = SecondaryLabel;
            SecondaryActionButton.IsEnabled = SecondaryCommand?.CanExecute(null) == true;
        }
        if (DangerActionButton is not null)
        {
            DangerActionButton.Content = DangerLabel;
            DangerActionButton.IsEnabled = DangerCommand?.CanExecute(DangerCommandParameter) == true;
        }

        // Re-check trigger button state so a disabled trigger doesn't open a useless menu.
        if (SecondaryActionButton?.IsEnabled != true && DangerActionButton?.IsEnabled != true)
        {
            return;
        }

        menu.Tag = this;
        menu.PlacementTarget = TriggerButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnSecondaryMenuClick(object sender, RoutedEventArgs e)
    {
        if (TriggerButton.ContextMenu?.Tag is OverflowMenuButton owner
            && owner.SecondaryCommand?.CanExecute(null) == true)
        {
            owner.SecondaryCommand.Execute(null);
        }
        if (TriggerButton.ContextMenu is { } menu) menu.IsOpen = false;
        e.Handled = true;
    }

    private void OnDangerMenuClick(object sender, RoutedEventArgs e)
    {
        if (TriggerButton.ContextMenu?.Tag is OverflowMenuButton owner
            && owner.DangerCommand?.CanExecute(owner.DangerCommandParameter) == true)
        {
            owner.DangerCommand.Execute(owner.DangerCommandParameter);
        }
        if (TriggerButton.ContextMenu is { } menu) menu.IsOpen = false;
        e.Handled = true;
    }
}
