namespace Stackroot.Core.AdminTools;

public enum MailpitAutoStartAction
{
    SkipDisabled,
    SkipNotInstalled,
    SkipAlreadyRunning,
    StartRequired
}

public static class MailpitAutoStartDecision
{
    public static MailpitAutoStartAction Decide(bool enabled, bool autoStart, bool installed, bool running)
    {
        if (!enabled || !autoStart)
        {
            return MailpitAutoStartAction.SkipDisabled;
        }

        if (!installed)
        {
            return MailpitAutoStartAction.SkipNotInstalled;
        }

        if (running)
        {
            return MailpitAutoStartAction.SkipAlreadyRunning;
        }

        return MailpitAutoStartAction.StartRequired;
    }
}
