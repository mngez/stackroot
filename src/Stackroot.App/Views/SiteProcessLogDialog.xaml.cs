using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Stackroot.App.Helpers;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views;

public partial class SiteProcessLogDialog : Window
{
    private const int MaxDisplayChars = 128 * 1024;
    private const int MaxIncrementalDeltaChars = 16 * 1024;

    private string? _lastAppliedContent;
    private int _applyGeneration;

    public SiteProcessLogDialog()
    {
        InitializeComponent();
        var (width, height) = LogDialogBoundsStore.Load();
        Width = width;
        Height = height;
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyLogContent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Interlocked.Increment(ref _applyGeneration);
        if (WindowState == WindowState.Normal)
        {
            LogDialogBoundsStore.Save(Width, Height);
        }

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldContext)
        {
            oldContext.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newContext)
        {
            newContext.PropertyChanged += OnViewModelPropertyChanged;
            _lastAppliedContent = null;
            ApplyLogContent();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SiteProcessLogDialogViewModel.LogContent) or nameof(FileLogDialogViewModel.LogContent))
        {
            ApplyLogContent();
        }
    }

    private string? GetLogContent() =>
        DataContext switch
        {
            SiteProcessLogDialogViewModel processVm => processVm.LogContent,
            FileLogDialogViewModel fileVm => fileVm.LogContent,
            _ => null
        };

    private void ApplyLogContent()
    {
        var content = GetLogContent();
        if (content is null || string.Equals(content, _lastAppliedContent, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _lastAppliedContent;
        var canAppend =
            previous is not null
            && !LogColorizer.ContainsAnsi(content)
            && content.StartsWith(previous, StringComparison.Ordinal)
            && content.Length > previous.Length
            && content.Length - previous.Length <= MaxIncrementalDeltaChars
            && content.Length <= MaxDisplayChars;

        var generation = Interlocked.Increment(ref _applyGeneration);
        var captured = content;
        var delta = canAppend ? content[previous!.Length..] : null;

        _ = Task.Run(() =>
            {
                if (delta is not null)
                {
                    return (Append: true, Segments: LogColorizer.ParseSegments(delta));
                }

                var display = LogColorizer.TrimTailForDisplay(captured, MaxDisplayChars);
                return (Append: false, Segments: LogColorizer.ParseSegments(display));
            })
            .ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled || generation != _applyGeneration)
                {
                    return;
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (generation != _applyGeneration)
                    {
                        return;
                    }

                    var current = GetLogContent();
                    if (!string.Equals(current, captured, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (task.Result.Append)
                    {
                        LogColorizer.ApplySegmentsAppend(LogViewer, task.Result.Segments);
                    }
                    else
                    {
                        LogColorizer.ApplySegments(LogViewer, task.Result.Segments);
                    }

                    _lastAppliedContent = captured;
                });
            }, TaskScheduler.Default);
    }
}
