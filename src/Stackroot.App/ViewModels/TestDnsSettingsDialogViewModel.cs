using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Dns;
using Stackroot.Core.Settings;
using System.Windows;

namespace Stackroot.App.ViewModels;

public sealed class TestDnsSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly TestDnsCoordinator? _testDns;
    private readonly bool _allowDangerousSettings;
    private bool _enabled;
    private bool _autoStart = true;
    private bool _logRequests;
    private string _suffixesText = ".test";
    private string _suffixesError = string.Empty;
    private string _dangerousSuffixWarning = string.Empty;
    private string _resolveAddress = LocalDnsResolveAddress.Default;
    private string _resolveAddressError = string.Empty;
    private bool _isSaving;
    private string _statusMessage = string.Empty;
    private string _saveButtonText = "Save";

    public TestDnsSettingsDialogViewModel(SettingsStore settingsStore, TestDnsCoordinator? testDns = null)
    {
        _settingsStore = settingsStore;
        _testDns = testDns;

        var settings = settingsStore.Load();
        _allowDangerousSettings = settings.TestDns.AllowDangerousSettings;
        _enabled = settings.TestDns.Enabled;
        _autoStart = settings.TestDns.AutoStart;
        _logRequests = settings.TestDns.LogRequests;
        _resolveAddress = LocalDnsResolveAddress.Normalize(settings.TestDns.ResolveAddress);
        _suffixesText = LocalDnsSuffix.FormatText(settings.TestDns.Suffixes);
        RefreshSuffixValidation();

        SaveCommand = new RelayCommand(_ => _ = SaveAsync(), _ => !IsSaving);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty), _ => !IsSaving);
        CopyRecoveryCommand = new RelayCommand(_ => CopyRecoveryCommandToClipboard());
    }

    public bool PowerUserModeEnabled => _allowDangerousSettings;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool LogRequests
    {
        get => _logRequests;
        set => SetProperty(ref _logRequests, value);
    }

    public string ResolveAddress
    {
        get => _resolveAddress;
        set
        {
            if (SetProperty(ref _resolveAddress, value))
            {
                ResolveAddressError = LocalDnsResolveAddress.Validate(value) ?? string.Empty;
            }
        }
    }

    public string ResolveAddressError
    {
        get => _resolveAddressError;
        private set => SetProperty(ref _resolveAddressError, value);
    }

    public string SuffixesText
    {
        get => _suffixesText;
        set
        {
            if (SetProperty(ref _suffixesText, value))
            {
                RefreshSuffixValidation();
            }
        }
    }

    public string SuffixesError
    {
        get => _suffixesError;
        private set => SetProperty(ref _suffixesError, value);
    }

    public string DangerousSuffixWarning
    {
        get => _dangerousSuffixWarning;
        private set => SetProperty(ref _dangerousSuffixWarning, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                CloseCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !IsSaving;

    public string SaveButtonText
    {
        get => _saveButtonText;
        private set => SetProperty(ref _saveButtonText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand CopyRecoveryCommand { get; }

    public string RecoveryCommandText => TestDnsRecoveryCommands.WindowsCleanupScript;

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private static void CopyRecoveryCommandToClipboard() =>
        Clipboard.SetText(TestDnsRecoveryCommands.WindowsCleanupScript);

    private void RefreshSuffixValidation()
    {
        SuffixesError = LocalDnsSuffix.ValidateText(SuffixesText, _allowDangerousSettings) ?? string.Empty;
        DangerousSuffixWarning = _allowDangerousSettings && LocalDnsSuffix.TextContainsCatchAll(SuffixesText)
            ? "Catch-all suffix \".\" routes all DNS on this PC through Stackroot. Internet and apps may break if the helper stops. Use with Log DNS queries for inspection."
            : string.Empty;
    }

    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        var suffixValidation = LocalDnsSuffix.ValidateText(SuffixesText, _allowDangerousSettings);
        if (suffixValidation is not null)
        {
            SuffixesError = suffixValidation;
            StatusMessage = suffixValidation;
            return;
        }

        var resolveValidation = LocalDnsResolveAddress.Validate(ResolveAddress);
        if (resolveValidation is not null)
        {
            ResolveAddressError = resolveValidation;
            StatusMessage = resolveValidation;
            return;
        }

        var suffixes = LocalDnsSuffix.ParseText(SuffixesText, _allowDangerousSettings);
        if (_allowDangerousSettings && LocalDnsSuffix.ContainsCatchAll(suffixes))
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            var confirmed = ConfirmDialog.Show(
                owner,
                "Route all DNS through Stackroot?",
                "Suffix \".\" sends every DNS query on this PC to 127.0.0.1:53. "
                + "If the DNS helper stops, websites and apps may fail to resolve names. "
                + "Enable Log DNS queries if you are inspecting traffic. Continue?",
                "Apply catch-all routing",
                isDanger: true);
            if (!confirmed)
            {
                StatusMessage = "Catch-all routing was not applied.";
                return;
            }
        }

        var resolveAddress = LocalDnsResolveAddress.Normalize(ResolveAddress);
        IsSaving = true;
        SaveButtonText = "Saving…";
        StatusMessage = _testDns is not null
            ? "Applying Test DNS settings… Windows may ask for administrator approval once."
            : "Saving settings…";
        var previousSettings = _settingsStore.Load().TestDns;

        try
        {
            _settingsStore.UpdateTestDns(new TestDnsSettings
            {
                Enabled = Enabled,
                AutoStart = AutoStart,
                LogRequests = LogRequests,
                AllowDangerousSettings = _allowDangerousSettings,
                ResolveAddress = resolveAddress,
                Suffixes = suffixes
            });

            if (_testDns is not null)
            {
                await _testDns.ApplySettingsAsync().ConfigureAwait(true);
            }

            StatusMessage = Enabled
                ? $"Test DNS enabled for {string.Join(", ", suffixes)}."
                : "Test DNS disabled.";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            try
            {
                _settingsStore.UpdateTestDns(previousSettings);
                if (_testDns is not null)
                {
                    await _testDns.ApplySettingsAsync().ConfigureAwait(true);
                }
            }
            catch
            {
                // Keep the original error visible; rollback is best-effort.
            }

            StatusMessage = $"Could not apply Test DNS settings. Changes were reverted. {ex.Message}";
        }
        finally
        {
            IsSaving = false;
            SaveButtonText = "Save";
        }
    }
}
