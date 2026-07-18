using Stackroot.Core.AdminTools;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class MailpitAutoStartPolicyTests
{
    [Theory]
    [InlineData(false, true, true, false, MailpitAutoStartAction.SkipDisabled)]
    [InlineData(true, false, true, false, MailpitAutoStartAction.SkipDisabled)]
    [InlineData(true, true, false, false, MailpitAutoStartAction.SkipNotInstalled)]
    [InlineData(true, true, true, true, MailpitAutoStartAction.SkipAlreadyRunning)]
    [InlineData(true, true, true, false, MailpitAutoStartAction.StartRequired)]
    public void Decide_covers_startup_states(
        bool enabled,
        bool autoStart,
        bool installed,
        bool running,
        MailpitAutoStartAction expected)
    {
        var actual = MailpitAutoStartDecision.Decide(enabled, autoStart, installed, running);
        Assert.Equal(expected, actual);
    }
}
