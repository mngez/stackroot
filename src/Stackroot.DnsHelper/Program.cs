using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stackroot.Core.Dns;
using Stackroot.DnsHelper;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = StackrootDnsHelperConstants.ServiceName;
});
builder.Services.AddHostedService<DnsHelperHostedService>();
await builder.Build().RunAsync();
