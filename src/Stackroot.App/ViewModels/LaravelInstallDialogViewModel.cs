using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.ViewModels;

public sealed class LaravelOptionItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class LaravelInstallDialogViewModel : ViewModelBase
{
    public string SiteName { get; }
    public string SiteDomain { get; }
    public string DatabaseName { get; set; }

    public ObservableCollection<LaravelOptionItem> StarterKits { get; } = [];
    public ObservableCollection<LaravelOptionItem> Stacks { get; } = [];
    public ObservableCollection<LaravelOptionItem> DatabaseEngines { get; } = [];

    private LaravelOptionItem? _selectedStarterKit;
    public LaravelOptionItem? SelectedStarterKit
    {
        get => _selectedStarterKit;
        set
        {
            if (SetProperty(ref _selectedStarterKit, value))
                RaisePropertyChanged(nameof(ShowStackOptions));
        }
    }

    private LaravelOptionItem? _selectedStack;
    public LaravelOptionItem? SelectedStack
    {
        get => _selectedStack;
        set => SetProperty(ref _selectedStack, value);
    }

    private LaravelOptionItem? _selectedEngine;
    public LaravelOptionItem? SelectedEngine
    {
        get => _selectedEngine;
        set => SetProperty(ref _selectedEngine, value);
    }

    public bool ShowStackOptions => _selectedStarterKit?.Id != "none";

    public bool RunNpmBuild { get; set; } = true;
    public bool RunMigrations { get; set; } = true;

    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }

    public LaravelInstallDialogViewModel(string siteName, string siteDomain)
    {
        SiteName = siteName;
        SiteDomain = siteDomain;
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in siteDomain)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sanitized.Append(c);
            else sanitized.Append('_');
        }
        DatabaseName = sanitized.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(DatabaseName)) DatabaseName = "laravel_db";

        LoadOptions();

        _selectedStarterKit = StarterKits.FirstOrDefault(k => k.Id == "none");
        _selectedStack = Stacks.FirstOrDefault(s => s.Id == "inertia");
        _selectedEngine = DatabaseEngines.FirstOrDefault(e => e.Id == "mysql");

        InstallCommand = new RelayCommand(_ =>
        {
            var result = new LaravelInstallResult(
                DatabaseName,
                SelectedStarterKit?.Id ?? "none",
                SelectedStack?.Id ?? "inertia",
                ParseEngine(SelectedEngine?.Id ?? "mysql"),
                RunNpmBuild,
                RunMigrations);
            RequestClose?.Invoke(this, result);
        });
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, null!));
    }

    private void LoadOptions()
    {
        try
        {
            // Same location as catalog.json — AppData resources (copied during bootstrap)
            var resourcesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot", "resources", "packages");
            var jsonPath = Path.Combine(resourcesRoot, "laravel-options.json");

            // Fall back to dev output directory if not in AppData
            if (!File.Exists(jsonPath))
            {
                jsonPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "resources", "packages", "laravel-options.json");
            }

            if (!File.Exists(jsonPath))
            {
                LoadDefaults();
                return;
            }

            var json = File.ReadAllText(jsonPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("starterKits", out var kits))
                foreach (var item in kits.EnumerateArray())
                    StarterKits.Add(new LaravelOptionItem { Id = item.GetProperty("id").GetString()!, Label = item.GetProperty("label").GetString()! });

            if (root.TryGetProperty("stacks", out var stacks))
                foreach (var item in stacks.EnumerateArray())
                    Stacks.Add(new LaravelOptionItem { Id = item.GetProperty("id").GetString()!, Label = item.GetProperty("label").GetString()! });

            if (root.TryGetProperty("databaseEngines", out var engines))
                foreach (var item in engines.EnumerateArray())
                    DatabaseEngines.Add(new LaravelOptionItem { Id = item.GetProperty("id").GetString()!, Label = item.GetProperty("label").GetString()! });
        }
        catch { LoadDefaults(); }
    }

    private void LoadDefaults()
    {
        if (StarterKits.Count == 0)
        {
            StarterKits.Add(new() { Id = "none", Label = "None (plain Laravel)" });
            StarterKits.Add(new() { Id = "breeze", Label = "Breeze" });
            StarterKits.Add(new() { Id = "jetstream", Label = "Jetstream" });
        }
        if (Stacks.Count == 0)
        {
            Stacks.Add(new() { Id = "inertia", Label = "Inertia (Vue)" });
            Stacks.Add(new() { Id = "livewire", Label = "Livewire (Blade)" });
            Stacks.Add(new() { Id = "api", Label = "API only" });
        }
        if (DatabaseEngines.Count == 0)
        {
            DatabaseEngines.Add(new() { Id = "mysql", Label = "MySQL" });
            DatabaseEngines.Add(new() { Id = "mariadb", Label = "MariaDB" });
            DatabaseEngines.Add(new() { Id = "postgresql", Label = "PostgreSQL" });
            DatabaseEngines.Add(new() { Id = "sqlite", Label = "SQLite" });
        }
    }

    private static SqlEngine ParseEngine(string id) => id.ToLowerInvariant() switch
    {
        "mariadb" => SqlEngine.Mariadb,
        "postgresql" => SqlEngine.Postgresql,
        "sqlite" => SqlEngine.Sqlite,
        _ => SqlEngine.Mysql,
    };

    public event EventHandler<LaravelInstallResult?>? RequestClose;
}

public sealed class LaravelInstallResult
{
    public string DatabaseName { get; }
    public string StarterKit { get; }
    public string Stack { get; }
    public SqlEngine DatabaseEngine { get; }
    public bool RunNpmBuild { get; }
    public bool RunMigrations { get; }

    public LaravelInstallResult(string dbName, string starterKit, string stack, SqlEngine engine, bool runNpm, bool runMigrations)
    {
        DatabaseName = dbName;
        StarterKit = starterKit;
        Stack = stack;
        DatabaseEngine = engine;
        RunNpmBuild = runNpm;
        RunMigrations = runMigrations;
    }
}
