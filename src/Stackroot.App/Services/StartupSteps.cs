namespace Stackroot.App.Services;

public static class StartupSteps
{
    public static readonly IReadOnlyList<(string Id, string Title)> FirstRun =
    [
        ("init", "Initializing application"),
        ("folders", "Creating data folders"),
        ("core", "Preparing configuration"),
        ("runtime", "Configuring runtimes and tools"),
        ("stack", "Starting web stack"),
        ("finalize", "Completing setup")
    ];
}
