using System.Windows;
using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class DevSslPathsDialogViewModel : ViewModelBase
{
    public DevSslPathsDialogViewModel(string certificatePath, string privateKeyPath)
    {
        CertificatePath = certificatePath;
        PrivateKeyPath = privateKeyPath;
        CopyCertificateCommand = new RelayCommand(_ => Copy(CertificatePath), _ => !string.IsNullOrWhiteSpace(CertificatePath));
        CopyPrivateKeyCommand = new RelayCommand(_ => Copy(PrivateKeyPath), _ => !string.IsNullOrWhiteSpace(PrivateKeyPath));
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string CertificatePath { get; }
    public string PrivateKeyPath { get; }

    public RelayCommand CopyCertificateCommand { get; }
    public RelayCommand CopyPrivateKeyCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;

    private static void Copy(string value) => Clipboard.SetText(value);
}
