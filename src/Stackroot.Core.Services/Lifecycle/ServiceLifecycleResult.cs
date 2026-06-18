namespace Stackroot.Core.Services.Lifecycle;

public sealed record ServiceLifecycleResult(bool Success, string? Message = null);
