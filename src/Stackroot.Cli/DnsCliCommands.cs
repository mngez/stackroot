using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Dns;

namespace Stackroot.Cli;

internal static class DnsCliCommands
{
    public static int Run(string[] args, ServiceProvider provider)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "serve" => RunServe(args[1..], provider).GetAwaiter().GetResult(),
            "probe" => RunProbe(args[1..]).GetAwaiter().GetResult(),
            "cleanup" => RunCleanup(),
            _ => Unknown(args[0])
        };
    }

    private static async Task<int> RunServe(string[] args, ServiceProvider provider)
    {
        var port = TestDnsServer.ListenPort;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--port" or "-p" && i + 1 < args.Length && int.TryParse(args[++i], out var parsed) && parsed > 0)
            {
                port = parsed;
            }
            else if (args[i] is "--help" or "-h")
            {
                PrintServeHelp();
                return 0;
            }
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintServeHelp();
                return 1;
            }
        }

        var paths = provider.GetRequiredService<StackrootPaths>();
        var settingsStore = provider.GetRequiredService<SettingsStore>();
        var settings = settingsStore.Load();
        var siteNames = LoadSiteServerNames(paths.DataRoot);
        var testDns = settings.TestDns;
        var suffixes = LocalDnsSuffix.NormalizeList(
            testDns.Suffixes,
            ensureDefaultTest: !LocalDnsSuffix.ContainsCatchAll(testDns.Suffixes),
            allowDangerous: testDns.AllowDangerousSettings);
        var options = LocalDnsServerOptions.Create(
            suffixes,
            LocalDnsCatalog.CollectNames(settings.General.AppDomain, siteNames),
            settings.TestDns.ResolveAddress);

        await using var server = new TestDnsServer(port);
        server.Configure(options);

        try
        {
            await server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not start DNS on 127.0.0.1:{port}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Local dev DNS on 127.0.0.1:{port} (UDP+TCP)");
        Console.WriteLine($"Suffixes: {string.Join(", ", suffixes)}");
        Console.WriteLine($"Local names: {options.LocalNames.Count}");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await server.StopAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunProbe(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintProbeHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var host = args[0];
        var server = IPAddress.Loopback;
        var port = TestDnsServer.ListenPort;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--port" or "-p" && i + 1 < args.Length && int.TryParse(args[++i], out var parsed) && parsed > 0)
            {
                port = parsed;
            }
            else if (args[i] is "--server" or "-s" && i + 1 < args.Length && IPAddress.TryParse(args[++i], out var address))
            {
                server = address;
            }
            else if (args[i] is "--help" or "-h")
            {
                PrintProbeHelp();
                return 0;
            }
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintProbeHelp();
                return 1;
            }
        }

        using var client = new UdpClient();
        client.Connect(server, port);
        await client.SendAsync(BuildAQuery(host)).ConfigureAwait(false);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            var answerCount = result.Buffer.Length >= 8
                ? BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(6))
                : 0;
            Console.WriteLine($"OK  {host} via {server}:{port} — {result.Buffer.Length} bytes, {answerCount} answer(s)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {host} via {server}:{port} — {ex.Message}");
            return 1;
        }
    }

    private static List<string> LoadSiteServerNames(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "sites.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("sites", out var sites) || sites.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var site in sites.EnumerateArray())
            {
                if (site.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                if (site.TryGetProperty("domain", out var domain) && domain.GetString() is { Length: > 0 } primary)
                {
                    names.Add(primary.Trim().ToLowerInvariant());
                }

                if (site.TryGetProperty("domainAliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alias in aliases.EnumerateArray())
                    {
                        if (alias.GetString() is { Length: > 0 } value)
                        {
                            names.Add(value.Trim().ToLowerInvariant());
                        }
                    }
                }
            }

            return names;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not read sites.json ({ex.Message}).");
            return [];
        }
    }

    private static byte[] BuildAQuery(string hostName)
    {
        var labels = hostName.Trim().TrimEnd('.').Split('.');
        var packet = new byte[512];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);
        var offset = 12;
        foreach (var label in labels)
        {
            packet[offset++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label).CopyTo(packet.AsSpan(offset));
            offset += label.Length;
        }

        packet[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 1);
        offset += 4;
        return packet[..offset];
    }

    private static int RunCleanup()
    {
        var nrpt = new WindowsNrptManager();
        if (!nrpt.HasAnyStackrootRules())
        {
            Console.WriteLine("No Stackroot DNS routing rules (NRPT) are present.");
            return 0;
        }

        if (!nrpt.TryDisable(out var error))
        {
            Console.Error.WriteLine(error ?? "Could not remove Stackroot DNS routing rules.");
            return 1;
        }

        Console.WriteLine("Removed Stackroot DNS routing rules (NRPT).");
        if (nrpt.HasAnyStackrootRules())
        {
            Console.Error.WriteLine("Some Stackroot DNS routing rules are still present.");
            return 1;
        }

        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown dns command: {command}");
        PrintHelp();
        return 1;
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            stackroot dns — local dev DNS utilities

              stackroot dns serve [--port PORT]
                  Run the dev DNS responder (UDP+TCP) using settings.json suffixes
                  and site domains from sites.json. Ctrl+C to stop.

              stackroot dns probe HOSTNAME [--server IP] [--port PORT]
                  Send a test A-record query to a DNS server (default 127.0.0.1:53).

              stackroot dns cleanup
                  Remove Stackroot NRPT routing rules (restores normal DNS for .test/.dev/.com).
            """);
    }

    private static void PrintServeHelp()
    {
        Console.WriteLine("Usage: stackroot dns serve [--port PORT]");
    }

    private static void PrintProbeHelp()
    {
        Console.WriteLine("Usage: stackroot dns probe HOSTNAME [--server IP] [--port PORT]");
    }
}
