using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.ViewModels;

public sealed class WordPressInstallDialogViewModel : ViewModelBase
{
    public WordPressInstallDialogViewModel(string siteTitle, string domain)
    {
        InstallCommand = new RelayCommand(_ =>
        {
            Result = new WordPressInstallInput
            {
                SiteTitle = SiteTitle.Trim(),
                AdminUser = AdminUser.Trim(),
                AdminPassword = AdminPassword,
                AdminEmail = AdminEmail.Trim(),
                DatabaseName = DatabaseName.Trim(),
                DatabaseEngine = SelectedEngine
            };
            RequestClose?.Invoke(this, EventArgs.Empty);
        }, _ => !string.IsNullOrWhiteSpace(SiteTitle)
            && !string.IsNullOrWhiteSpace(AdminUser)
            && !string.IsNullOrWhiteSpace(AdminPassword)
            && !string.IsNullOrWhiteSpace(AdminEmail)
            && !string.IsNullOrWhiteSpace(DatabaseName));

        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        SiteTitle = siteTitle;
        AdminUser = "admin";
        AdminPassword = Guid.NewGuid().ToString("N")[..12];
        AdminEmail = "admin@" + domain;
        DatabaseName = SanitizeDbName(domain);
        SelectedEngine = SqlEngine.Mysql;
    }

    private string _siteTitle = string.Empty;
    public string SiteTitle
    {
        get => _siteTitle;
        set { if (SetProperty(ref _siteTitle, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    private string _adminUser = string.Empty;
    public string AdminUser
    {
        get => _adminUser;
        set { if (SetProperty(ref _adminUser, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    private string _adminPassword = string.Empty;
    public string AdminPassword
    {
        get => _adminPassword;
        set { if (SetProperty(ref _adminPassword, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    private string _adminEmail = string.Empty;
    public string AdminEmail
    {
        get => _adminEmail;
        set { if (SetProperty(ref _adminEmail, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    private string _databaseName = string.Empty;
    public string DatabaseName
    {
        get => _databaseName;
        set { if (SetProperty(ref _databaseName, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    private SqlEngine _selectedEngine;
    public SqlEngine SelectedEngine
    {
        get => _selectedEngine;
        set => SetProperty(ref _selectedEngine, value);
    }

    public WordPressInstallInput? Result { get; private set; }

    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler? RequestClose;

    private static string SanitizeDbName(string domain)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in domain)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        var name = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "wp_site" : name;
    }
}

public sealed class WordPressInstallInput
{
    public string SiteTitle { get; init; } = "";
    public string AdminUser { get; init; } = "";
    public string AdminPassword { get; init; } = "";
    public string AdminEmail { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public SqlEngine DatabaseEngine { get; init; } = SqlEngine.Mysql;
}
