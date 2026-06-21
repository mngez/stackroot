using Microsoft.Extensions.DependencyInjection;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Migrations;
using Stackroot.Core.IO.Storage;
using Stackroot.Core.Settings;

namespace Stackroot.Engine;

public static class StackrootEngineServiceCollectionExtensions
{
    public static IServiceCollection AddStackrootEngineCore(this IServiceCollection services)
    {
        services.AddSingleton(_ => StackrootPathResolver.Resolve());
        services.AddSingleton<IJsonFileStore, JsonFileStore>();
        services.AddSingleton<SettingsStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var json = provider.GetRequiredService<IJsonFileStore>();
            return new SettingsStore(paths.DataRoot, json);
        });
        return services;
    }

    public static DataMigrationReport RunDataMigrations(IServiceProvider services, bool allowRepeat = false)
    {
        var paths = services.GetRequiredService<StackrootPaths>();
        return DataMigrationRunner.Run(paths, allowRepeat: allowRepeat);
    }

    public static bool TryValidateSettings(IServiceProvider services, out string message)
    {
        var settingsStore = services.GetRequiredService<SettingsStore>();
        settingsStore.TryLoad(out _, out var issue);
        if (issue == SettingsLoadIssue.None)
        {
            message = "Settings loaded successfully.";
            return true;
        }

        message = issue == SettingsLoadIssue.Corrupted
            ? "Settings file is corrupted; defaults are in use until repaired."
            : "Settings could not be loaded.";
        return false;
    }
}
