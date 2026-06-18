using Stackroot.App.ViewModels;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.ViewModels;

internal static class ServiceStatusPresenter
{
    public static void Apply(ServiceEntryViewModel item, bool isRunning, string? liveMessage = null)
    {
        if (item.IsInstalling)
        {
            item.StatusText = "Installing...";
            item.StatusColor = "#E8C06A";
            item.NotifyRunningStateChanged();
            return;
        }

        if (!item.Enabled)
        {
            item.StatusText = "Disabled in settings";
            item.StatusColor = "#91A0B5";
            item.NotifyRunningStateChanged();
            return;
        }

        if (!item.Installed)
        {
            item.StatusText = "Not installed";
            item.StatusColor = "#91A0B5";
            item.NotifyRunningStateChanged();
            return;
        }

        if (isRunning)
        {
            item.StatusText = item.Definition.Runtime == ServiceRuntime.Library ? "Ready" : "Running";
            item.StatusColor = "#8FD6B6";
            item.NotifyRunningStateChanged();
            return;
        }

        if (!string.IsNullOrWhiteSpace(liveMessage))
        {
            item.StatusText = "Error";
            item.StatusColor = "#E88A92";
            item.Message = liveMessage;
            item.NotifyRunningStateChanged();
            return;
        }

        item.StatusText = "Stopped";
        item.StatusColor = "#91A0B5";
        item.NotifyRunningStateChanged();
    }
}
