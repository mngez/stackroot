using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO;
using Stackroot.Core.Sites.Models;

namespace Stackroot.App.Helpers;

internal static class CustomCommandsPortability
{
    private const int ExportSchemaVersion = 1;
    private const string ExportKindValue = "stackroot-custom-commands";
    private const string ExportFileFilter = "Stackroot custom commands (*.stackroot-commands.json)|*.stackroot-commands.json|JSON files (*.json)|*.json";

    public static bool TryExport(Window? owner, string siteDataDir, IReadOnlyList<SiteCustomCommand> commands)
    {
        if (commands.Count == 0)
        {
            MessageDialog.Show(
                owner,
                "Export custom commands",
                "There are no commands to export.",
                StackrootDialogKind.Info);
            return false;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export custom commands",
            Filter = ExportFileFilter,
            FileName = "custom-commands.stackroot-commands.json",
            DefaultExt = ".stackroot-commands.json"
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return false;
        }

        var document = new ExportDocument
        {
            SchemaVersion = ExportSchemaVersion,
            Commands = commands.Select(command => ToExportEntry(siteDataDir, command)).ToList()
        };

        var json = JsonSerializer.Serialize(document, JsonSerializerConfig.Default);
        File.WriteAllText(dialog.FileName, json);
        return true;
    }

    public static IReadOnlyList<SiteCustomCommand>? TryImport(Window? owner, string siteDataDir)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import custom commands",
            Filter = ExportFileFilter
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            return ParseImportFile(json, siteDataDir);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(
                owner,
                "Import custom commands",
                $"Could not read the file:{Environment.NewLine}{ex.Message}",
                StackrootDialogKind.Warning);
            return null;
        }
    }

    private static IReadOnlyList<SiteCustomCommand> ParseImportFile(string json, string siteDataDir)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (IsExportDocument(root))
        {
            var export = JsonSerializer.Deserialize<ExportDocument>(json, JsonSerializerConfig.Default);
            if (export?.Commands is { Count: > 0 })
            {
                return export.Commands
                    .Select(entry => ToSiteCommand(siteDataDir, entry))
                    .Where(static command => !string.IsNullOrWhiteSpace(command.Label)
                        && !string.IsNullOrWhiteSpace(command.Command))
                    .ToList();
            }

            return [];
        }

        var siteDocument = JsonSerializer.Deserialize<CustomCommandsDocument>(json, JsonSerializerConfig.Default);
        if (siteDocument?.Commands is not { Count: > 0 })
        {
            return [];
        }

        return siteDocument.Commands
            .Select(entry => new SiteCustomCommand
            {
                Label = entry.Label.Trim(),
                Command = entry.Command.Trim(),
                Runtime = entry.Runtime,
                ForegroundHex = CustomCommandChromeHelper.NormalizeHex(entry.ForegroundHex),
                BackgroundHex = CustomCommandChromeHelper.NormalizeHex(entry.BackgroundHex)
            })
            .Where(static command => !string.IsNullOrWhiteSpace(command.Label)
                && !string.IsNullOrWhiteSpace(command.Command))
            .ToList();
    }

    private static bool IsExportDocument(JsonElement root) =>
        root.TryGetProperty("exportKind", out var kind)
        && string.Equals(kind.GetString(), ExportKindValue, StringComparison.Ordinal);

    private static ExportEntry ToExportEntry(string siteDataDir, SiteCustomCommand command)
    {
        var entry = new ExportEntry
        {
            Label = command.Label,
            Command = command.Command,
            Runtime = command.Runtime,
            ForegroundHex = command.ForegroundHex,
            BackgroundHex = command.BackgroundHex
        };

        var iconPath = CustomCommandIconStore.ResolvePath(siteDataDir, command.IconFileName);
        if (iconPath is null)
        {
            return entry;
        }

        entry.IconBase64 = Convert.ToBase64String(File.ReadAllBytes(iconPath));
        entry.IconExtension = Path.GetExtension(iconPath);
        return entry;
    }

    private static SiteCustomCommand ToSiteCommand(string siteDataDir, ExportEntry entry)
    {
        var command = new SiteCustomCommand
        {
            Label = entry.Label.Trim(),
            Command = entry.Command.Trim(),
            Runtime = entry.Runtime,
            ForegroundHex = CustomCommandChromeHelper.NormalizeHex(entry.ForegroundHex),
            BackgroundHex = CustomCommandChromeHelper.NormalizeHex(entry.BackgroundHex)
        };

        if (string.IsNullOrWhiteSpace(entry.IconBase64))
        {
            return command;
        }

        try
        {
            var bytes = Convert.FromBase64String(entry.IconBase64);
            if (bytes.Length > 0)
            {
                command.IconFileName = CustomCommandIconStore.ImportIconFromBytes(
                    siteDataDir,
                    command.Id,
                    bytes,
                    entry.IconExtension);
            }
        }
        catch (FormatException)
        {
            // Skip invalid icon data.
        }

        return command;
    }

    private sealed class ExportDocument
    {
        public string ExportKind { get; set; } = ExportKindValue;

        public int SchemaVersion { get; set; } = ExportSchemaVersion;

        public List<ExportEntry> Commands { get; set; } = [];
    }

    private sealed class ExportEntry
    {
        public string Label { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public string? Runtime { get; set; }

        public string? ForegroundHex { get; set; }

        public string? BackgroundHex { get; set; }

        public string? IconBase64 { get; set; }

        public string? IconExtension { get; set; }
    }
}
