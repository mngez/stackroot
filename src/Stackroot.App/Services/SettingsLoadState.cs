using System.IO;
using Stackroot.Core.Settings;

namespace Stackroot.App.Services;

public sealed class SettingsLoadState
{
    public SettingsLoadIssue Issue { get; private set; } = SettingsLoadIssue.None;

    public bool ShowCorruptedSettingsBanner => Issue == SettingsLoadIssue.Corrupted;

    public bool CanPersistSettings => Issue == SettingsLoadIssue.None;

    public bool ShowRestoreBackupButton =>
        Issue == SettingsLoadIssue.Corrupted && !string.IsNullOrWhiteSpace(LatestBackupPath);

    public string? LatestBackupPath { get; private set; }

    public string CorruptedSettingsBannerText { get; private set; } =
        "Stackroot could not read settings.json. Default settings are in use until you repair or replace the file.";

    public void Initialize(SettingsStore store)
    {
        if (store.TryLoad(out _, out var issue))
        {
            Issue = SettingsLoadIssue.None;
            LatestBackupPath = null;
            return;
        }

        Issue = issue;
        LatestBackupPath = store.FindLatestBackupPath();
        if (File.Exists(store.Path))
        {
            CorruptedSettingsBannerText =
                $"Stackroot could not read '{store.Path}'. Default settings are shown — saving is disabled until you restore a backup.";
            if (!string.IsNullOrWhiteSpace(LatestBackupPath))
            {
                CorruptedSettingsBannerText +=
                    $" Latest backup: {Path.GetFileName(LatestBackupPath)}.";
            }
        }
    }

    public void MarkRestoredSuccessfully()
    {
        Issue = SettingsLoadIssue.None;
        LatestBackupPath = null;
        CorruptedSettingsBannerText = string.Empty;
    }
}
