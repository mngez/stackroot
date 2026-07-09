using System.IO;
using Stackroot.Core.Sites.Persistence;

namespace Stackroot.App.Services;

public sealed class SitesLoadState
{
    public bool IsCorrupted { get; private set; }

    public bool ShowCorruptedSitesBanner => IsCorrupted;

    public bool ShowRestoreBackupButton =>
        IsCorrupted && !string.IsNullOrWhiteSpace(LatestBackupPath);

    public string? LatestBackupPath { get; private set; }

    public string CorruptedSitesBannerText { get; private set; } =
        "Stackroot could not read sites.json. Your site list is shown as empty until you repair or replace the file.";

    public void Initialize(SiteStore store)
    {
        if (store.TryLoad(out _, out _))
        {
            IsCorrupted = false;
            LatestBackupPath = null;
            return;
        }

        IsCorrupted = true;
        LatestBackupPath = store.FindLatestBackupPath();
        if (File.Exists(store.SitesFilePath))
        {
            CorruptedSitesBannerText =
                $"Stackroot could not read '{store.SitesFilePath}'. Your site list is shown as empty — saving is disabled until you restore a backup.";
            if (!string.IsNullOrWhiteSpace(LatestBackupPath))
            {
                CorruptedSitesBannerText +=
                    $" Latest backup: {Path.GetFileName(LatestBackupPath)}.";
            }
        }
    }

    public void MarkRestoredSuccessfully()
    {
        IsCorrupted = false;
        LatestBackupPath = null;
        CorruptedSitesBannerText = string.Empty;
    }
}
