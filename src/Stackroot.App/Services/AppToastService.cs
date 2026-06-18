using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Stackroot.App.Services;

public sealed class AppToastService : Core.Services.IToastService
{
    private readonly Core.Abstractions.IDiagnosticsReporter? _diagnostics;

    public AppToastService(Core.Abstractions.IDiagnosticsReporter? diagnostics = null)
    {
        _diagnostics = diagnostics;
    }

    public void Show(string title, string message)
    {
        _diagnostics?.LogActivity("Toast", $"{title}: {message}");

        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                var trayIcon = new Forms.NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Visible = true,
                    BalloonTipTitle = title,
                    BalloonTipText = message,
                    BalloonTipIcon = Forms.ToolTipIcon.Info
                };
                trayIcon.ShowBalloonTip(5000);
                trayIcon.BalloonTipClosed += (_, _) => trayIcon.Dispose();
            }
            catch
            {
                // Silently fail
            }
        });
    }
}
