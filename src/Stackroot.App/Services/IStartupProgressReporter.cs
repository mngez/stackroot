namespace Stackroot.App.Services;

public interface IStartupProgressReporter
{
    void BeginStep(string stepId, string title);

    void CompleteStep(string stepId);

    void FailStep(string stepId, string? message = null);
}
