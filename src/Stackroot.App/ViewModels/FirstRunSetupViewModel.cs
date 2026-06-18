using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.App.Services;

namespace Stackroot.App.ViewModels;

public sealed class FirstRunSetupViewModel : ViewModelBase, IStartupProgressReporter
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, StartupStepViewModel> _steps = new(StringComparer.OrdinalIgnoreCase);
    private string _subheadline = "Preparing your local development environment…";
    private bool _isComplete;
    private bool _hasFailed;
    private bool _userDismissed;

    public FirstRunSetupViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Steps = new ObservableCollection<StartupStepViewModel>();
        OpenStackrootCommand = new RelayCommand(_ => OpenStackrootRequested?.Invoke(), _ => IsComplete && !HasFailed);

        foreach (var (id, title) in StartupSteps.FirstRun)
        {
            var step = new StartupStepViewModel { Id = id, Title = title };
            _steps[id] = step;
            Steps.Add(step);
        }
    }

    public event Action? OpenStackrootRequested;

    public RelayCommand OpenStackrootCommand { get; }

    public ObservableCollection<StartupStepViewModel> Steps { get; }

    public string Headline => "Setting up Stackroot for the first time";

    public string Subheadline
    {
        get => _subheadline;
        private set => SetProperty(ref _subheadline, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            if (SetProperty(ref _isComplete, value))
            {
                RaisePropertyChanged(nameof(ShowOpenButton));
                OpenStackrootCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasFailed
    {
        get => _hasFailed;
        private set
        {
            if (SetProperty(ref _hasFailed, value))
            {
                RaisePropertyChanged(nameof(AllowClose));
                RaisePropertyChanged(nameof(ShowOpenButton));
                OpenStackrootCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowOpenButton => IsComplete && !HasFailed;

    public bool AllowClose => HasFailed || _userDismissed;

    public void MarkDismissed()
    {
        _userDismissed = true;
        RaisePropertyChanged(nameof(AllowClose));
    }

    public void BeginStep(string stepId, string title)
    {
        RunOnUi(() =>
        {
            var step = ResolveStep(stepId, title);
            step.Status = StartupStepStatus.Running;
            step.Detail = null;
            Subheadline = title;
        });
    }

    public void CompleteStep(string stepId)
    {
        RunOnUi(() =>
        {
            if (!_steps.TryGetValue(stepId, out var step))
            {
                return;
            }

            step.Status = StartupStepStatus.Completed;
            step.Detail = null;
        });
    }

    public void FailStep(string stepId, string? message = null)
    {
        RunOnUi(() =>
        {
            HasFailed = true;
            Subheadline = string.IsNullOrWhiteSpace(message)
                ? "Setup could not be completed."
                : message;

            if (!_steps.TryGetValue(stepId, out var step))
            {
                return;
            }

            step.Status = StartupStepStatus.Failed;
            step.Detail = message;
        });
    }

    public void SetComplete()
    {
        RunOnUi(() =>
        {
            IsComplete = true;
            Subheadline = "Setup complete. Click Open Stackroot when you're ready.";
        });
    }

    private StartupStepViewModel ResolveStep(string stepId, string title)
    {
        if (_steps.TryGetValue(stepId, out var existing))
        {
            return existing;
        }

        var step = new StartupStepViewModel { Id = stepId, Title = title };
        _steps[stepId] = step;
        Steps.Add(step);
        return step;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }
}
