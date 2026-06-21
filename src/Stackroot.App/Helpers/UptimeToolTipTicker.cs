using System.Windows.Threading;

namespace Stackroot.App.Helpers;

public static class UptimeToolTipTicker
{
    private static readonly HashSet<IUptimeTooltipTarget> Active = [];
    private static DispatcherTimer? _timer;

    public static void Register(IUptimeTooltipTarget target)
    {
        Active.Add(target);
        target.RefreshUptimeDisplay();
        EnsureTimer();
    }

    public static void Unregister(IUptimeTooltipTarget target)
    {
        Active.Remove(target);
        if (Active.Count == 0)
        {
            StopTimer();
        }
    }

    private static void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        foreach (var target in Active.ToArray())
        {
            target.RefreshUptimeDisplay();
        }
    }

    private static void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer = null;
    }
}
