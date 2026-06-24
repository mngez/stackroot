using Microsoft.Extensions.Hosting;
using Stackroot.Core.Dns;

namespace Stackroot.DnsHelper;

public sealed class DnsHelperHostedService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var loop = new DnsHelperHostedLoop();
        await loop.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
