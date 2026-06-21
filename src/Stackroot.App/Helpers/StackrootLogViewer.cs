using System.Diagnostics;
using System.IO;
using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.Helpers;

public static class StackrootLogViewer
{
    public const string CtrlClickEditorToolTip = "View log (Ctrl+click: open in editor)";

    public static void Open(
        string logPath,
        string title,
        bool openInExternalEditor,
        SettingsStore settingsStore,
        Window? owner = null,
        Func<Task>? cancelAsync = null,
        Func<bool>? isRunning = null)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return;
        }

        if (openInExternalEditor)
        {
            OpenInPreferredEditor(logPath, settingsStore);
        }
        else
        {
            ShowInApp(logPath, title, owner, cancelAsync, isRunning);
        }
    }

    public static void ShowInApp(
        string logPath,
        string title,
        Window? owner = null,
        Func<Task>? cancelAsync = null,
        Func<bool>? isRunning = null)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return;
        }

        owner ??= Application.Current?.MainWindow;
        var dialogVm = new FileLogDialogViewModel(logPath, title, cancelAsync, isRunning);
        var dialog = new SiteProcessLogDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => dialogVm.Dispose();
        dialog.Show();
    }

    public static void OpenInPreferredEditor(string logPath, SettingsStore settingsStore)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return;
        }

        var settings = settingsStore.Load();
        var editor = settings.General.PreferredEditor ?? PreferredEditor.System;
        string? exe = editor switch
        {
            PreferredEditor.Vscode => ResolveInPath("code"),
            PreferredEditor.Cursor => ResolveInPath("cursor"),
            PreferredEditor.NotepadPlusPlus => ResolveInPath("notepad++"),
            PreferredEditor.Custom => settings.General.CustomEditorPath,
            _ => null
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"\"{logPath}\"",
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static string? ResolveInPath(string name) =>
        File.Exists($@"C:\Program Files\{name}\{name}.exe") ? $@"C:\Program Files\{name}\{name}.exe"
        : File.Exists($@"C:\Users\{Environment.UserName}\AppData\Local\Programs\{name}\{name}.exe")
            ? $@"C:\Users\{Environment.UserName}\AppData\Local\Programs\{name}\{name}.exe"
            : null;
}
