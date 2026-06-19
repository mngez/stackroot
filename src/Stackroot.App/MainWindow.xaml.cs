using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Forms = System.Windows.Forms;

namespace Stackroot.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly SettingsStore _settingsStore;
    private readonly StackrootShutdownCoordinator _shutdown;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayManagedIcon;
    private bool _quitting;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public MainWindow(
        ShellViewModel viewModel,
        DashboardViewModel dashboardViewModel,
        SettingsStore settingsStore,
        StackrootShutdownCoordinator shutdown)
    {
        _viewModel = viewModel;
        _dashboardViewModel = dashboardViewModel;
        _settingsStore = settingsStore;
        _shutdown = shutdown;
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SetupTrayIcon();
        Loaded += OnWindowLoaded;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            ResetMainScrollPosition();
        }
    }

    private void ResetMainScrollPosition()
    {
        MainContentScrollViewer.ScrollToVerticalOffset(0);
        MainContentScrollViewer.ScrollToHorizontalOffset(0);
        Dispatcher.BeginInvoke(
            () =>
            {
                MainContentScrollViewer.ScrollToVerticalOffset(0);
                MainContentScrollViewer.ScrollToHorizontalOffset(0);
            },
            DispatcherPriority.Loaded);
    }

    private void OnNavigationItemPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem { DataContext: NavigationItem nav })
        {
            nav.Command.Execute(null);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        WindowsTheme.ApplyDarkTitleBar(this);
        _viewModel.EnsureInitialPageLoaded();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        // Refresh featured sites nav after startup completes
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // wait for deferred startup
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var siteManager = ((App)Application.Current).Services.GetRequiredService<SiteManager>();
                    _viewModel.RefreshSiteNavFromStore(siteManager);
                }
                catch { }
            });
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting || !IsLoaded)
        {
            base.OnClosing(e);
            return;
        }

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher.HasShutdownStarted == true)
        {
            base.OnClosing(e);
            return;
        }

        var behavior = _settingsStore.Load().General.CloseBehavior ?? CloseBehavior.Ask;
        switch (behavior)
        {
            case CloseBehavior.Quit:
                e.Cancel = true;
                _ = ShutdownAndQuitAsync();
                return;

            case CloseBehavior.Background:
                e.Cancel = true;
                HideToTray();
                return;

            default:
                e.Cancel = true;
                var choice = StackrootDialogs.AskCloseBehavior(this);

                if (choice == StackrootDialogResult.Yes)
                {
                    _ = ShutdownAndQuitAsync();
                }
                else if (choice == StackrootDialogResult.No)
                {
                    HideToTray();
                }

                return;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _trayManagedIcon?.Dispose();
        base.OnClosed(e);
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Stackroot",
            Visible = true
        };
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Show", null, (_, _) => ShowFromTray());
        var startOrRestartAllItem = _trayMenu.Items.Add("Start all", null, (_, _) => RunTrayCommand(_dashboardViewModel.StartOrRestartAllCommand));
        var stopAllItem = _trayMenu.Items.Add("Stop all", null, (_, _) => RunTrayCommand(_dashboardViewModel.StopAllCommand));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Quit", null, (_, _) => _ = ShutdownAndQuitAsync());
        _trayMenu.Opening += (_, _) =>
        {
            startOrRestartAllItem.Text = _dashboardViewModel.StartOrRestartAllLabel;
            startOrRestartAllItem.Enabled = _dashboardViewModel.StartOrRestartAllCommand.CanExecute(null);
            stopAllItem.Enabled = _dashboardViewModel.StopAllCommand.CanExecute(null);
        };
        _trayIcon.ContextMenuStrip = _trayMenu;

        var trayAsset = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.png");
        if (File.Exists(trayAsset))
        {
            _trayManagedIcon = CreateIconFromPng(trayAsset);
            if (_trayManagedIcon is not null)
            {
                _trayIcon.Icon = _trayManagedIcon;
            }
        }

        _trayIcon.DoubleClick += (_, _) =>
        {
            ShowFromTray();
        };
    }

    private void RunTrayCommand(System.Windows.Input.ICommand command)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        });
    }

    private void BeginQuit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void ShowShutdownOverlayIfNeeded()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray();
        }

        _viewModel.ShowShutdownOverlay("Closing services...");
    }

    public async Task QuitForUpdateInstallerAsync(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (_quitting)
        {
            return;
        }

        BeginQuit();
        ShowShutdownOverlayIfNeeded();
        _viewModel.ShowShutdownOverlay("Closing Stackroot before update…");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        try
        {
            await Task.Run(() => _shutdown.ShutdownAsync(TimeSpan.FromSeconds(15))).ConfigureAwait(true);
        }
        catch
        {
            // OnExit performs a second best-effort pass.
        }

        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    private async Task ShutdownAndQuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        BeginQuit();
        ShowShutdownOverlayIfNeeded();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        try
        {
            await Task.Run(() => _shutdown.ShutdownAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(true);
        }
        catch
        {
            // OnExit performs a second best-effort pass.
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = "Stackroot is still running";
            _trayIcon.BalloonTipText = "Use tray icon > Quit to stop services and exit.";
            _trayIcon.ShowBalloonTip(3000);
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private static System.Drawing.Icon? CreateIconFromPng(string path)
    {
        using var bitmap = new Bitmap(path);
        var hIcon = bitmap.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
