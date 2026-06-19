using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class SslTrustPromptViewModel : ViewModelBase
{
    private bool _showBanner;
    private string _bannerText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private Func<Task>? _trustAsync;
    private Action? _dismiss;

    public SslTrustPromptViewModel()
    {
        TrustCertificateCommand = new RelayCommand(
            _ => _ = (_trustAsync?.Invoke() ?? Task.CompletedTask),
            _ => CanTrust);
        DismissCommand = new RelayCommand(
            _ => _dismiss?.Invoke(),
            _ => ShowBanner && !_isBusy);
    }

    public RelayCommand TrustCertificateCommand { get; }
    public RelayCommand DismissCommand { get; }

    public bool ShowBanner
    {
        get => _showBanner;
        private set
        {
            if (SetProperty(ref _showBanner, value))
            {
                RaisePropertyChanged(nameof(CanTrust));
                TrustCertificateCommand.RaiseCanExecuteChanged();
                DismissCommand.RaiseCanExecuteChanged();
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
                RaisePropertyChanged(nameof(CanTrust));
                TrustCertificateCommand.RaiseCanExecuteChanged();
                DismissCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanTrust => ShowBanner && !_isBusy;

    internal void Bind(Func<Task> trustAsync, Action dismiss)
    {
        _trustAsync = trustAsync;
        _dismiss = dismiss;
    }

    internal void PresentPrompt()
    {
        BannerText = "HTTPS sites use a local Stackroot certificate. Trust it once to avoid browser \"Your connection is not private\" warnings.";
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
