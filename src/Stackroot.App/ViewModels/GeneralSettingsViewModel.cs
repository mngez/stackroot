using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Windows;

namespace Stackroot.App.ViewModels;

public sealed class GeneralSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly SiteManager _siteManager;
    private readonly StackrootBinManager _binManager;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly SessionActivityReporter _activity;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly SemaphoreSlim _saveSync = new(1, 1);
    private int _saveVersion;
    private bool _suppressAutoSave;
    private bool _isTrustingSsl;
    private string _wwwPath = string.Empty;
    private string _defaultWwwPath = string.Empty;
    private string _appDomain = string.Empty;
    private PreferredEditor _preferredEditor = PreferredEditor.System;
    private string _customEditorPath = string.Empty;
    private CloseBehavior _closeBehavior = CloseBehavior.Background;
    private int _logRetentionDays = 30;
    private bool _addBinToPath;
    private bool _thumbnailsEnabled;
    private bool _launchAtStartup;
    private string _statusMessage = string.Empty;

    public GeneralSettingsViewModel(
        SettingsStore settingsStore,
        SiteManager siteManager,
        StackrootBinManager binManager,
        AppDomainConfigWriter appDomainConfigWriter,
        SessionActivityReporter activity)
    {
        _settingsStore = settingsStore;
        _siteManager = siteManager;
        _binManager = binManager;
        _appDomainConfigWriter = appDomainConfigWriter;
        _activity = activity;
        TrustSslCommand = new RelayCommand(_ => _ = TrustSslAsync(), _ => !IsTrustingSsl);
        BrowseWwwCommand = new RelayCommand(_ => BrowseWww());
        BrowseEditorCommand = new RelayCommand(_ => BrowseEditor(), _ => PreferredEditor == PreferredEditor.Custom);
        UseDefaultWwwCommand = new RelayCommand(_ => WwwPath = string.Empty);

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            Save();
        };

        Reload();
    }

    public RelayCommand TrustSslCommand { get; }
    public RelayCommand BrowseWwwCommand { get; }
    public RelayCommand BrowseEditorCommand { get; }
    public RelayCommand UseDefaultWwwCommand { get; }

    public IReadOnlyList<EditorOptionViewModel> EditorOptions { get; } =
    [
        new(PreferredEditor.System, "Windows default"),
        new(PreferredEditor.Vscode, "Visual Studio Code"),
        new(PreferredEditor.Cursor, "Cursor"),
        new(PreferredEditor.NotepadPlusPlus, "Notepad++"),
        new(PreferredEditor.Custom, "Custom executable…")
    ];

    public bool ShowCustomEditorPath => PreferredEditor == PreferredEditor.Custom;

    public string WwwPath
    {
        get => _wwwPath;
        set
        {
            if (SetProperty(ref _wwwPath, value))
            {
                ScheduleAutoSave();
            }
        }
    }

    public string DefaultWwwPath
    {
        get => _defaultWwwPath;
        private set => SetProperty(ref _defaultWwwPath, value);
    }

    public string AppDomain
    {
        get => _appDomain;
        set
        {
            if (SetProperty(ref _appDomain, value))
            {
                ScheduleAutoSave();
            }
        }
    }

    public PreferredEditor PreferredEditor
    {
        get => _preferredEditor;
        set
        {
            if (SetProperty(ref _preferredEditor, value))
            {
                RaisePropertyChanged(nameof(ShowCustomEditorPath));
                BrowseEditorCommand.RaiseCanExecuteChanged();
                Save();
            }
        }
    }

    public string CustomEditorPath
    {
        get => _customEditorPath;
        set
        {
            if (SetProperty(ref _customEditorPath, value))
            {
                ScheduleAutoSave();
            }
        }
    }

    public CloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set
        {
            if (SetProperty(ref _closeBehavior, value))
            {
                Save();
            }
        }
    }

    public int LogRetentionDays
    {
        get => _logRetentionDays;
        set
        {
            if (SetProperty(ref _logRetentionDays, value))
            {
                ScheduleAutoSave();
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                Save();
                ApplyStartupSetting(value);
            }
        }
    }

    public bool AddBinToPath
    {
        get => _addBinToPath;
        set
        {
            if (SetProperty(ref _addBinToPath, value))
            {
                Save();
            }
        }
    }

    public bool ThumbnailsEnabled
    {
        get => _thumbnailsEnabled;
        set
        {
            if (SetProperty(ref _thumbnailsEnabled, value))
            {
                Save();
            }
        }
    }

    public string BinPathPreview => Path.Combine(StackrootPathResolver.Resolve(ensureDirectories: false).RuntimeRoot, "bin");

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsTrustingSsl
    {
        get => _isTrustingSsl;
        private set
        {
            if (SetProperty(ref _isTrustingSsl, value))
            {
                TrustSslCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void Reload()
    {
        _suppressAutoSave = true;
        try
        {
            var settings = _settingsStore.Load();
            WwwPath = settings.General.WwwPath ?? string.Empty;
            DefaultWwwPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "www");
            AppDomain = settings.General.AppDomain ?? "stackroot.test";
            PreferredEditor = settings.General.PreferredEditor ?? PreferredEditor.System;
            CustomEditorPath = settings.General.CustomEditorPath ?? string.Empty;
            CloseBehavior = settings.General.CloseBehavior ?? CloseBehavior.Ask;
            LogRetentionDays = settings.General.LogRetentionDays ?? 30;
            AddBinToPath = settings.General.AddBinToPath ?? false;
            ThumbnailsEnabled = settings.General.ThumbnailsEnabled ?? false;
            LaunchAtStartup = settings.General.LaunchAtStartup ?? false;
            StatusMessage = string.Empty;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void Save()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        var settings = BuildGeneralSettingsSnapshot();
        var version = Interlocked.Increment(ref _saveVersion);
        _ = SaveAsync(settings, version);
    }

    private GeneralSettings BuildGeneralSettingsSnapshot() => new()
    {
        WwwPath = string.IsNullOrWhiteSpace(WwwPath) ? null : WwwPath.Trim(),
        AppDomain = string.IsNullOrWhiteSpace(AppDomain) ? "stackroot.test" : AppDomain.Trim(),
        PreferredEditor = PreferredEditor,
        CustomEditorPath = PreferredEditor == PreferredEditor.Custom && !string.IsNullOrWhiteSpace(CustomEditorPath)
            ? CustomEditorPath.Trim()
            : null,
        CloseBehavior = CloseBehavior,
        LogRetentionDays = LogRetentionDays < 0 ? 0 : LogRetentionDays,
        AddBinToPath = AddBinToPath,
        ThumbnailsEnabled = ThumbnailsEnabled,
        LaunchAtStartup = LaunchAtStartup
    };

    private async Task SaveAsync(GeneralSettings settings, int version)
    {
        await _saveSync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (version < Volatile.Read(ref _saveVersion))
            {
                return;
            }

            await Task.Run(() =>
            {
                _settingsStore.UpdateGeneral(settings);
                _appDomainConfigWriter.Write();
            }).ConfigureAwait(false);

            await _binManager.SyncStackrootBinAsync().ConfigureAwait(false);

            if (version == Volatile.Read(ref _saveVersion))
            {
                await RunOnUiAsync(() =>
                {
                    StatusMessage = "Saved";
                    _activity.LogInfo("Settings", SessionActivityMessages.GeneralSettingsSaved());
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                StatusMessage = $"Save failed: {ex.Message}";
                _activity.LogError("Settings", StatusMessage);
            }).ConfigureAwait(false);
        }
        finally
        {
            _saveSync.Release();
        }
    }

    private void BrowseWww()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select WWW folder",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(WwwPath) ? DefaultWwwPath : WwwPath
        };

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            WwwPath = dialog.SelectedPath;
        }
    }

    private void BrowseEditor()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select editor executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(CustomEditorPath) ? string.Empty : Path.GetFileName(CustomEditorPath),
            InitialDirectory = string.IsNullOrWhiteSpace(CustomEditorPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : Path.GetDirectoryName(CustomEditorPath)
        };

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            CustomEditorPath = dialog.FileName;
        }
    }

    private async Task TrustSslAsync()
    {
        if (IsTrustingSsl)
        {
            return;
        }

        IsTrustingSsl = true;
        try
        {
            var result = await Task.Run(_siteManager.TrustDevSslCertificate).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                StatusMessage = SessionActivityMessages.SslCertificateTrust(result.Ok, result.Message);
                if (result.Ok)
                {
                    _activity.LogSuccess("Settings", StatusMessage);
                }
                else
                {
                    _activity.LogError("Settings", StatusMessage);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                StatusMessage = ex.Message;
                _activity.LogError("Settings", StatusMessage);
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiAsync(() => IsTrustingSsl = false).ConfigureAwait(false);
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    private static void ApplyStartupSetting(bool enable)
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var shortcutPath = Path.Combine(startupDir, "Stackroot.lnk");

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                try { CreateShortcut(exePath, shortcutPath); } catch { }
            }
        }
        else
        {
            try { File.Delete(shortcutPath); } catch { }
        }
    }

    private static void CreateShortcut(string targetPath, string shortcutPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"$WshShell = New-Object -ComObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('{shortcutPath}'); $Shortcut.TargetPath = '{targetPath}'; $Shortcut.Save()\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
    }

    public void Dispose()
    {
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            Save();
        }
    }
}

public sealed record EditorOptionViewModel(PreferredEditor Id, string Label);
