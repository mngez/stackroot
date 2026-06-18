using System.Windows;

namespace Stackroot.App.Views;

public partial class ChangePasswordDialog : Window
{
    public string? Password { get; private set; }

    public ChangePasswordDialog()
    {
        InitializeComponent();
        PasswordInput.Focus();
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordInput.Password;
        if (string.IsNullOrWhiteSpace(Password))
        {
            MessageBox.Show("Enter a password.", "Change password", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
