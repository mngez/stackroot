using Microsoft.Extensions.DependencyInjection;
using Stackroot.Cli;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO.Migrations;
using Stackroot.Engine;

return StackrootCliProgram.Run(args);

internal static class StackrootCliProgram
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var services = new ServiceCollection();
        services.AddStackrootEngineCore();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        return args[0].ToLowerInvariant() switch
        {
            "migrate" => RunMigrate(provider),
            "settings-check" => RunSettingsCheck(provider),
            "health" => RunHealth(provider),
            "dns" => DnsCliCommands.Run(args[1..], provider),
            _ => UnknownCommand(args[0])
        };
    }

    private static int RunMigrate(ServiceProvider provider)
    {
        var report = StackrootEngineServiceCollectionExtensions.RunDataMigrations(provider, allowRepeat: true);
        Console.WriteLine(report.HasChanges
            ? $"Migration complete ({report.Changes.Count} change(s))."
            : "Migration complete (no changes).");
        return 0;
    }

    private static int RunSettingsCheck(ServiceProvider provider)
    {
        var ok = StackrootEngineServiceCollectionExtensions.TryValidateSettings(provider, out var message);
        Console.WriteLine(message);
        return ok ? 0 : 2;
    }

    private static int RunHealth(ServiceProvider provider)
    {
        var paths = provider.GetRequiredService<StackrootPaths>();
        Console.WriteLine($"dataRoot={paths.DataRoot}");
        Console.WriteLine($"runtimeRoot={paths.RuntimeRoot}");

        var settingsOk = StackrootEngineServiceCollectionExtensions.TryValidateSettings(provider, out var settingsMessage);
        Console.WriteLine($"settings={settingsMessage}");

        var migrationReport = StackrootEngineServiceCollectionExtensions.RunDataMigrations(provider, allowRepeat: true);
        Console.WriteLine(migrationReport.HasChanges
            ? $"migrations={migrationReport.Changes.Count} change(s)"
            : "migrations=ok");

        Console.WriteLine(DiagnosticsCounters.FormatSummary());
        return settingsOk ? 0 : 2;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static bool IsHelp(string command)
        => command is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Stackroot CLI (headless)

            Usage:
              stackroot migrate          Run JSON data migrations
              stackroot settings-check   Validate settings.json
              stackroot health           Migrate + settings + paths summary (CI)
              stackroot dns serve        Run local dev DNS (from settings + sites)
              stackroot dns probe HOST   Test an A-record query against 127.0.0.1:53
              stackroot dns cleanup      Remove Stackroot NRPT dev-DNS routing rules
              stackroot help             Show this help
            """);
    }
}
