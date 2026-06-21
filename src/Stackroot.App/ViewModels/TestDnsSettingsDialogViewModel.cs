using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class TestDnsSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly TestDnsCoordinator? _testDns;
    private bool _enabled;
    private bool _autoStart = true;
    private bool _isSaving;
    private string _statusMessage = string.Empty;
    private string _saveButtonText = "Save";

    public TestDnsSettingsDialogViewModel(SettingsStore settingsStore, TestDnsCoordinator? testDns = null)
    {
        _settingsStore = settingsStore;
        _testDns = testDns;

        var settings = settingsStore.Load();
        _enabled = settings.TestDns.Enabled;
        _autoStart = settings.TestDns.AutoStart;

        SaveCommand = new RelayCommand(_ => _ = SaveAsync(), _ => !IsSaving);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty), _ => !IsSaving);
    }

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

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

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
                AutoStart = AutoStart
            });

            if (_testDns is not null)
            {
                await _testDns.ApplySettingsAsync().ConfigureAwait(true);
            }

            StatusMessage = Enabled
                ? "Test DNS enabled. Wildcard .test names resolve via 127.0.0.1; other domains are unchanged."
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
