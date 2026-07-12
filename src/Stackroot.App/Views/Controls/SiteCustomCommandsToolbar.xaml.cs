using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Controls;

public partial class SiteCustomCommandsToolbar : UserControl
{
    public static readonly DependencyProperty ShowOpenSiteButtonProperty =
        DependencyProperty.Register(
            nameof(ShowOpenSiteButton),
            typeof(bool),
            typeof(SiteCustomCommandsToolbar),
            new PropertyMetadata(false, OnShowButtonsChanged));

    public static readonly DependencyProperty ShowOpenAdminButtonProperty =
        DependencyProperty.Register(
            nameof(ShowOpenAdminButton),
            typeof(bool),
            typeof(SiteCustomCommandsToolbar),
            new PropertyMetadata(false, OnShowButtonsChanged));

    private SiteManageViewModel? _viewModel;

    public SiteCustomCommandsToolbar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public bool ShowOpenSiteButton
    {
        get => (bool)GetValue(ShowOpenSiteButtonProperty);
        set => SetValue(ShowOpenSiteButtonProperty, value);
    }

    public bool ShowOpenAdminButton
    {
        get => (bool)GetValue(ShowOpenAdminButtonProperty);
        set => SetValue(ShowOpenAdminButtonProperty, value);
    }

    private static void OnShowButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SiteCustomCommandsToolbar)d).RebuildCommandButtons();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CustomCommandItems.CollectionChanged -= OnCustomCommandItemsChanged;
        }

        _viewModel = e.NewValue as SiteManageViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CustomCommandItems.CollectionChanged += OnCustomCommandItemsChanged;
        }

        RebuildCommandButtons();
    }

    private void OnCustomCommandItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildCommandButtons();

    private void RebuildCommandButtons()
    {
        CommandsPanel.Children.Clear();

        if (_viewModel is null)
        {
            return;
        }

        if (ShowOpenSiteButton)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 4, 8, 4),
                Padding = new Thickness(10, 6, 10, 6),
                Style = (Style)FindResource("StackrootPrimaryButtonStyle"),
                Command = _viewModel.OpenSiteCommand,
            };
            button.SetResourceReference(ContentControl.ContentProperty, "Loc.Common.OpenSite");
            CommandsPanel.Children.Add(button);
        }

        if (ShowOpenAdminButton)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 4, 8, 4),
                Padding = new Thickness(10, 6, 10, 6),
                Command = _viewModel.OpenPostInstallAdminCommand,
            };
            button.SetResourceReference(ContentControl.ContentProperty, "Loc.Common.OpenAdmin");
            CommandsPanel.Children.Add(button);
        }

        var customCommandStyle = (Style)FindResource("StackrootCustomCommandButtonStyle");
        var boolToVis = (IValueConverter)FindResource("BoolToVis");

        foreach (var item in _viewModel.CustomCommandItems)
        {
            var icon = new System.Windows.Controls.Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
            };
            icon.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding(nameof(item.IconSource)) { Source = item });
            icon.SetBinding(VisibilityProperty, new Binding(nameof(item.HasIcon)) { Source = item, Converter = boolToVis });

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(item.DisplayLabel)) { Source = item });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(label);

            var button = new Button
            {
                Margin = new Thickness(0, 4, 8, 4),
                Padding = new Thickness(10, 6, 10, 6),
                Style = customCommandStyle,
                DataContext = item,
                Content = content,
                Command = item.RunCommand,
            };
            button.SetBinding(ToolTipProperty, new Binding(nameof(item.Command)) { Source = item });
            CommandsPanel.Children.Add(button);
        }

        var manageButton = new Button
        {
            Margin = new Thickness(0, 4, 8, 4),
            Width = 32,
            Height = 32,
            Style = (Style)FindResource("StackrootPrimaryIconButtonStyle"),
            Command = _viewModel.ManageCustomCommandsCommand,
            Content = new TextBlock
            {
                FontFamily = (System.Windows.Media.FontFamily)FindResource("StackrootIconFont"),
                FontSize = 14,
                Text = "\uE8FD",
            },
        };
        manageButton.SetResourceReference(ToolTipProperty, "Loc.Common.ManageCustomCommands");
        CommandsPanel.Children.Add(manageButton);
    }

    private void OnCustomCommandViewLogClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: SiteCustomCommandViewModel cmd })
        {
            return;
        }

        cmd.OpenLog(System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control);
        e.Handled = true;
    }
}
