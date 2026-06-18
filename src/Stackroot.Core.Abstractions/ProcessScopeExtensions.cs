namespace Stackroot.Core.Abstractions;

public static class ProcessScopeExtensions
{
    public static string ToScopeKey(this ProcessScope scope)
    {
        return scope.Type == ProcessScopeType.Site
            ? $"{scope.SiteId}:{scope.ProcessId}"
            : $"global:{scope.ProcessId}";
    }
}
