using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class AppUpdateViewModel : ViewModelBase
{
    private bool _showBanner;
    private string _bannerText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private Func<Task>? _applyUpdateAsync;
    private Action? _dismiss;

    public AppUpdateViewModel()
    {
        ApplyUpdateCommand = new RelayCommand(
            _ => _ = (_applyUpdateAsync?.Invoke() ?? Task.CompletedTask),
            _ => CanApplyUpdate);
        DismissUpdateCommand = new RelayCommand(
            _ => _dismiss?.Invoke(),
            _ => ShowBanner && !_isBusy);
    }

    public RelayCommand ApplyUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }

    public bool ShowBanner
    {
        get => _showBanner;
        private set
        {
            if (SetProperty(ref _showBanner, value))
            {
                RaisePropertyChanged(nameof(CanApplyUpdate));
                ApplyUpdateCommand.RaiseCanExecuteChanged();
                DismissUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BannerText
    {
        get => _bannerText;
        private set => SetProperty(ref _bannerText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(CanApplyUpdate));
                ApplyUpdateCommand.RaiseCanExecuteChanged();
                DismissUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanApplyUpdate => ShowBanner && !_isBusy;

    internal void Bind(Func<Task> applyUpdateAsync, Action dismiss)
    {
        _applyUpdateAsync = applyUpdateAsync;
        _dismiss = dismiss;
    }

    internal void PresentUpdate(string currentVersion, string latestVersion)
    {
        BannerText = $"Stackroot {latestVersion} is available (you have {currentVersion}).";
        StatusText = string.Empty;
        ShowBanner = true;
    }

    internal void ClearBanner()
    {
        ShowBanner = false;
        BannerText = string.Empty;
        StatusText = string.Empty;
        IsBusy = false;
    }

    internal void SetBusy(string statusText)
    {
        IsBusy = true;
        StatusText = statusText;
    }

    internal void SetStatus(string statusText) => StatusText = statusText;

    internal void FinishBusy() => IsBusy = false;
}
