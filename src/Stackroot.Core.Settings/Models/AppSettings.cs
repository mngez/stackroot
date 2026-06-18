namespace Stackroot.Core.Settings.Models;

public sealed class AppSettings
{
    public string DataRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stackroot");
}
