using Stackroot.App.Scheduling;

namespace Stackroot.App.ViewModels;

public interface IScheduledTaskRowHost
{
    void UpdateTask(ScheduledTaskModel model);

    Task RunNowAndWaitAsync(string id);

    void DeleteTask(string id);

    void ReloadTasks();

    void OpenTaskLog(string taskId, string logPath, string label, bool openInExternalEditor);
}
