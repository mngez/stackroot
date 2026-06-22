using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Media = System.Windows.Media;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Views;
using Stackroot.Core.Sites.Models;

namespace Stackroot.App.ViewModels;

public sealed class CustomCommandEditItemViewModel : ViewModelBase
{
    private readonly string _siteDataDir;
    private string _label = string.Empty;
    private string _command = string.Empty;
    private string? _foregroundHex;
    private string? _backgroundHex;
    private string? _iconFileName;
    private string? _iconFilePath;
    private ImageSource? _iconSource;

    public CustomCommandEditItemViewModel(string siteDataDir, SiteCustomCommand model, Func<string, bool> isRunning)
    {
        _siteDataDir = siteDataDir;
        Id = model.Id;
        Label = model.Label;
        Command = model.Command;
        Runtime = model.Runtime;
        ForegroundHex = model.ForegroundHex ?? string.Empty;
        BackgroundHex = model.BackgroundHex ?? string.Empty;
        IconFileName = model.IconFileName;
        IsRunning = isRunning(Id);
        RefreshIconPreview();
    }

    public string Id { get; }

    public string? Runtime { get; set; }

    public bool IsRunning { get; private set; }

    public void RefreshRunningState(Func<string, bool> isRunning) =>
        IsRunning = isRunning(Id);

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Command
    {
        get => _command;
        set => SetProperty(ref _command, value);
    }

    public string ForegroundHex
    {
        get => _foregroundHex ?? string.Empty;
        set
        {
            if (SetProperty(ref _foregroundHex, value))
            {
                RaiseChromeChanged();
            }
        }
    }

    public string BackgroundHex
    {
        get => _backgroundHex ?? string.Empty;
        set
        {
            if (SetProperty(ref _backgroundHex, value))
            {
                RaiseChromeChanged();
            }
        }
    }

    public string? IconFileName
    {
        get => _iconFileName;
        private set => SetProperty(ref _iconFileName, value);
    }

    public ImageSource? IconSource
    {
        get => _iconSource;
        private set
        {
            if (SetProperty(ref _iconSource, value))
            {
                RaisePropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => IconSource is not null;

    public bool HasCustomChrome =>
        CustomCommandChromeHelper.HasCustomChrome(ForegroundHex, BackgroundHex, _iconFilePath);

    public Media.Brush? PreviewForegroundBrush =>
        CustomCommandChromeHelper.TryBrush(ForegroundHex);

    public Media.Brush? PreviewBackgroundBrush =>
        CustomCommandChromeHelper.TryBrush(BackgroundHex);

    public Media.Brush? CustomForegroundBrush => PreviewForegroundBrush;

    public Media.Brush? CustomBackgroundBrush => PreviewBackgroundBrush;

    public string PreviewLabel => string.IsNullOrWhiteSpace(Label) ? "Preview" : Label;

    public bool CanDelete => !IsRunning;

    public void ClearColors()
    {
        ForegroundHex = string.Empty;
        BackgroundHex = string.Empty;
    }

    public void ClearIcon()
    {
        if (!string.IsNullOrWhiteSpace(IconFileName))
        {
            CustomCommandIconStore.DeleteIcon(_siteDataDir, IconFileName);
        }

        IconFileName = null;
        _iconFilePath = null;
        IconSource = null;
        RaiseChromeChanged();
    }

    public bool TryImportIcon(Window? owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose button icon",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico;*.webp|All files|*.*"
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return false;
        }

        IconFileName = CustomCommandIconStore.ImportIcon(_siteDataDir, Id, dialog.FileName);
        RefreshIconPreview();
        return true;
    }

    public SiteCustomCommand ToModel() => new()
    {
        Id = Id,
        Label = Label.Trim(),
        Command = Command.Trim(),
        Runtime = Runtime,
        ForegroundHex = CustomCommandChromeHelper.NormalizeHex(ForegroundHex),
        BackgroundHex = CustomCommandChromeHelper.NormalizeHex(BackgroundHex),
        IconFileName = IconFileName
    };

    private void RefreshIconPreview()
    {
        _iconFilePath = CustomCommandIconStore.ResolvePath(_siteDataDir, IconFileName);
        IconSource = CustomCommandChromeHelper.TryIcon(_iconFilePath);
        RaiseChromeChanged();
    }

    private void RaiseChromeChanged()
    {
        RaisePropertyChanged(nameof(HasCustomChrome));
        RaisePropertyChanged(nameof(PreviewForegroundBrush));
        RaisePropertyChanged(nameof(PreviewBackgroundBrush));
        RaisePropertyChanged(nameof(CustomForegroundBrush));
        RaisePropertyChanged(nameof(CustomBackgroundBrush));
        RaisePropertyChanged(nameof(PreviewLabel));
    }
}

public sealed class CustomCommandsDialogViewModel : ViewModelBase
{
    private readonly string _siteDataDir;
    private readonly Func<string, bool> _isRunning;
    private readonly Dictionary<string, string?> _originalIcons;
    private CustomCommandEditItemViewModel? _selected;

    public CustomCommandsDialogViewModel(
        string siteDataDir,
        IEnumerable<SiteCustomCommand> commands,
        Func<string, bool> isRunning)
    {
        _siteDataDir = siteDataDir;
        _isRunning = isRunning;
        _originalIcons = commands.ToDictionary(static command => command.Id, static command => command.IconFileName, StringComparer.Ordinal);

        foreach (var command in commands)
        {
            Commands.Add(new CustomCommandEditItemViewModel(siteDataDir, command, isRunning));
        }

        AddCommand = new RelayCommand(_ => AddNewCommand());
        DeleteCommand = new RelayCommand(_ => DeleteSelected(), _ => Selected?.CanDelete == true);
        PickForegroundColorCommand = new RelayCommand(_ => PickForegroundColor(), _ => Selected is not null);
        PickBackgroundColorCommand = new RelayCommand(_ => PickBackgroundColor(), _ => Selected is not null);
        BrowseIconCommand = new RelayCommand(_ => BrowseIcon(), _ => Selected is not null);
        ClearIconCommand = new RelayCommand(_ => Selected?.ClearIcon(), _ => Selected?.HasIcon == true);
        ClearColorsCommand = new RelayCommand(_ => Selected?.ClearColors(), _ => Selected is not null);
        ExportCommand = new RelayCommand(_ => ExportCommands(), _ => Commands.Count > 0);
        ImportCommand = new RelayCommand(_ => ImportCommands());
        SaveCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true), _ => IsValid);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));

        Selected = Commands.FirstOrDefault();
    }

    public ObservableCollection<CustomCommandEditItemViewModel> Commands { get; } = [];

    public CustomCommandEditItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            DeleteCommand.RaiseCanExecuteChanged();
            BrowseIconCommand.RaiseCanExecuteChanged();
            ClearIconCommand.RaiseCanExecuteChanged();
            ClearColorsCommand.RaiseCanExecuteChanged();
            PickForegroundColorCommand.RaiseCanExecuteChanged();
            PickBackgroundColorCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsValid =>
        Commands.All(static item =>
            !string.IsNullOrWhiteSpace(item.Label) && !string.IsNullOrWhiteSpace(item.Command));

    public RelayCommand AddCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand PickForegroundColorCommand { get; }
    public RelayCommand PickBackgroundColorCommand { get; }
    public RelayCommand BrowseIconCommand { get; }
    public RelayCommand ClearIconCommand { get; }
    public RelayCommand ClearColorsCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<SiteCustomCommand> ToModels()
    {
        CleanupRemovedIcons();
        return Commands.Select(static item => item.ToModel()).ToList();
    }

    private void AddNewCommand()
    {
        var model = new SiteCustomCommand();
        var item = new CustomCommandEditItemViewModel(_siteDataDir, model, _isRunning);
        Commands.Add(item);
        Selected = item;
        SaveCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void ExportCommands()
    {
        CustomCommandsPortability.TryExport(
            Application.Current?.MainWindow,
            _siteDataDir,
            Commands.Select(static item => item.ToModel()).ToList());
    }

    private void ImportCommands()
    {
        var imported = CustomCommandsPortability.TryImport(Application.Current?.MainWindow, _siteDataDir);
        if (imported is null || imported.Count == 0)
        {
            if (imported is not null)
            {
                MessageDialog.Show(
                    Application.Current?.MainWindow,
                    "Import custom commands",
                    "The file does not contain any commands.",
                    StackrootDialogKind.Info);
            }

            return;
        }

        var owner = Application.Current?.MainWindow;
        var message = imported.Count == 1
            ? "Import 1 command and add it to this site?"
            : $"Import {imported.Count} commands and add them to this site?";
        if (!ConfirmDialog.Show(owner, "Import custom commands", message, "Import"))
        {
            return;
        }

        foreach (var model in imported)
        {
            Commands.Add(new CustomCommandEditItemViewModel(_siteDataDir, model, _isRunning));
        }

        Selected = Commands.LastOrDefault();
        SaveCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item)
        {
            return;
        }

        item.RefreshRunningState(_isRunning);
        if (!item.CanDelete)
        {
            MessageDialog.Show(
                Application.Current?.MainWindow,
                "Custom commands",
                "Stop the command before removing it.",
                StackrootDialogKind.Warning);
            return;
        }

        var index = Commands.IndexOf(item);
        item.ClearIcon();
        Commands.Remove(item);
        Selected = Commands.Count == 0
            ? null
            : Commands[Math.Clamp(index, 0, Commands.Count - 1)];
        SaveCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void BrowseIcon()
    {
        if (Selected is null)
        {
            return;
        }

        Selected.TryImportIcon(Application.Current?.MainWindow);
        ClearIconCommand.RaiseCanExecuteChanged();
    }

    private void PickForegroundColor()
    {
        if (Selected is null)
        {
            return;
        }

        var hex = ColorPickerDialog.PickHex(Application.Current?.MainWindow, Selected.ForegroundHex);
        if (hex is not null)
        {
            Selected.ForegroundHex = hex;
        }
    }

    private void PickBackgroundColor()
    {
        if (Selected is null)
        {
            return;
        }

        var hex = ColorPickerDialog.PickHex(Application.Current?.MainWindow, Selected.BackgroundHex);
        if (hex is not null)
        {
            Selected.BackgroundHex = hex;
        }
    }

    private void CleanupRemovedIcons()
    {
        var remaining = Commands.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, iconFileName) in _originalIcons)
        {
            if (!remaining.Contains(id))
            {
                CustomCommandIconStore.DeleteIcon(_siteDataDir, iconFileName);
            }
        }
    }
}
