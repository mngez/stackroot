using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Sites.Installers;

internal static class SiteInstallerProcessRunner
{
    public static async Task RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDir,
        Action<InstallerMessage> onMessage,
        CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                psi.Environment[key] = value;
            }
        }

        psi.Environment["COMPOSER_NO_INTERACTION"] = "1";
        psi.Environment["COMPOSER_DISABLE_XDEBUG"] = "1";
        psi.Environment["COMPOSER_ALLOW_SUPERUSER"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {fileName}");

        var stderr = new StringBuilder();
        var stdoutTask = PumpLinesAsync(process.StandardOutput, onMessage, isError: false, cancel);
        var stderrTask = PumpLinesAsync(process.StandardError, onMessage, isError: true, cancel, stderr);

        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr.ToString().Trim() : $"Process exited with code {process.ExitCode}.";
            throw new InvalidOperationException(detail);
        }
    }

    private static async Task PumpLinesAsync(
        StreamReader reader,
        Action<InstallerMessage> onMessage,
        bool isError,
        CancellationToken cancel,
        StringBuilder? capture = null)
    {
        while (!cancel.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancel).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            capture?.AppendLine(line);
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            onMessage(new InstallerMessage
            {
                Kind = isError ? InstallerMessageKind.Warning : InstallerMessageKind.Progress,
                Text = trimmed
            });
        }
    }
}
