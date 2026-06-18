using System.Windows;
using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class DatabaseEnvSnippetDialogViewModel : ViewModelBase
{
    public DatabaseEnvSnippetDialogViewModel(string databaseName, string snippet)
    {
        DatabaseName = databaseName;
        Snippet = snippet;
        CopyCommand = new RelayCommand(_ => Copy(), _ => !string.IsNullOrWhiteSpace(Snippet));
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string DatabaseName { get; }
    public string Snippet { get; }

    public RelayCommand CopyCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;

    private void Copy()
    {
        Clipboard.SetText(Snippet);
    }
}
