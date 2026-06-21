using Stackroot.Core.Abstractions;
using Stackroot.Core.Services;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class ServicePortReconcilePolicyTests
{
    [Theory]
    [InlineData(PortProbeResult.Closed, false)]
    [InlineData(PortProbeResult.Open, false)]
    public void ShouldSkipSupervisionRestart_never_skips_definite_probe(PortProbeResult probe, bool expected)
    {
        Assert.Equal(expected, ServicePortReconcilePolicy.ShouldSkipSupervisionRestart(probe));
    }

    [Fact]
    public void ShouldSkipSupervisionRestart_skips_inconclusive_only_with_live_tracked_process()
    {
        var stale = new ServiceInfo { PortOpen = true, Status = ServiceStatus.Running };
        Assert.False(ServicePortReconcilePolicy.ShouldSkipSupervisionRestart(PortProbeResult.Inconclusive, stale));
        Assert.False(ServicePortReconcilePolicy.ShouldSkipSupervisionRestart(PortProbeResult.Inconclusive, null));
    }

    [Theory]
    [InlineData(PortProbeResult.Open, true)]
    [InlineData(PortProbeResult.Closed, false)]
    [InlineData(PortProbeResult.Inconclusive, false)]
    public void ShouldSkipSupervisionRestartWhenPortBusy_detects_open_port(PortProbeResult probe, bool expected)
    {
        Assert.Equal(expected, ServicePortReconcilePolicy.ShouldSkipSupervisionRestartWhenPortBusy(probe));
    }

    [Theory]
    [InlineData(PortProbeResult.Closed, true)]
    [InlineData(PortProbeResult.Open, true)]
    [InlineData(null, true)]
    public void ShouldClearTrackedService_clears_on_definite_probe(PortProbeResult? probe, bool expected)
    {
        var cached = new ServiceInfo { PortOpen = true, Status = ServiceStatus.Running, Pid = 42 };
        Assert.Equal(expected, ServicePortReconcilePolicy.ShouldClearTrackedService(probe, cached));
    }

    [Fact]
    public void ShouldClearTrackedService_clears_inconclusive_when_no_live_pid()
    {
        var cached = new ServiceInfo { PortOpen = true, Status = ServiceStatus.Running };
        Assert.True(ServicePortReconcilePolicy.ShouldClearTrackedService(PortProbeResult.Inconclusive, cached));
    }

    [Fact]
    public void InferPortOpen_uses_pids_first()
    {
        var cached = new ServiceInfo { PortOpen = false, Status = ServiceStatus.Stopped };
        Assert.True(ServicePortReconcilePolicy.InferPortOpen(true, PortProbeResult.Closed, cached));
    }

    [Fact]
    public void InferPortOpen_treats_inconclusive_as_down_without_live_pid()
    {
        var cached = new ServiceInfo { PortOpen = true, Status = ServiceStatus.Running };
        Assert.False(ServicePortReconcilePolicy.InferPortOpen(false, PortProbeResult.Inconclusive, cached));
    }

    [Fact]
    public void InferPortOpen_does_not_assume_running_without_cache()
    {
        Assert.False(ServicePortReconcilePolicy.InferPortOpen(false, PortProbeResult.Inconclusive, null));
    }

    [Fact]
    public void InferPortOpen_treats_closed_as_down_even_with_stale_cache()
    {
        var cached = new ServiceInfo { PortOpen = true, Status = ServiceStatus.Running };
        Assert.False(ServicePortReconcilePolicy.InferPortOpen(false, PortProbeResult.Closed, cached));
    }

    [Fact]
    public void InferPortOpen_does_not_treat_foreign_open_port_as_running()
    {
        Assert.False(ServicePortReconcilePolicy.InferPortOpen(false, PortProbeResult.Open, null));
    }

    [Fact]
    public void IsPortBlockedByOtherApplication_detects_foreign_listener()
    {
        Assert.True(ServicePortReconcilePolicy.IsPortBlockedByOtherApplication(0, PortProbeResult.Open, null));
        Assert.False(ServicePortReconcilePolicy.IsPortBlockedByOtherApplication(1, PortProbeResult.Open, null));
        Assert.False(ServicePortReconcilePolicy.IsPortBlockedByOtherApplication(0, PortProbeResult.Closed, null));
    }

    [Fact]
    public void TryPreserveRunningWithoutPids_returns_false_without_live_pid()
    {
        IReadOnlyList<int> ownedPids = [];
        var cached = new ServiceInfo { PortOpen = true, Status = ServiceStatus.Running };

        Assert.False(ServicePortReconcilePolicy.TryPreserveRunningWithoutPids(cached, ref ownedPids));
        Assert.Empty(ownedPids);
    }
}
