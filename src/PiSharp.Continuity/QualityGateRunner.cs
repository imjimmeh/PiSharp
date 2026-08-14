using System.Diagnostics;
using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Executes a user-defined shell quality gate via
/// <c>System.Diagnostics.Process</c> with a per-gate timeout and retry/
/// backoff. The working directory is the session cwd (injected); a clean
/// process environment is used and no harness shell surface is needed.
/// </summary>
public sealed class QualityGateRunner
{
    private readonly string _workingDirectory;

    public QualityGateRunner(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public async Task<QualityGateResult> RunAsync(
        QualityGate gate,
        int defaultTimeoutSeconds,
        int defaultRetries,
        int backoffSeconds,
        CancellationToken ct)
    {
        var timeoutSeconds = gate.TimeoutSeconds > 0 ? gate.TimeoutSeconds : defaultTimeoutSeconds;
        var retries = gate.Retries >= 0 ? gate.Retries : defaultRetries;
        var attempts = 0;
        string? tail = null;

        while (true)
        {
            attempts++;
            var result = await RunOnceAsync(gate.Command, timeoutSeconds, ct).ConfigureAwait(false);
            tail = result.OutputTail;
            if (result.Succeeded) return new QualityGateResult(gate.Id, true, attempts, tail);

            if (attempts > retries)
                return new QualityGateResult(gate.Id, false, attempts, tail);

            if (backoffSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task<(bool Succeeded, string? OutputTail)> RunOnceAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        using var process = new Process();
        var fileName = ResolveShell(out var argPrefix);
        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = argPrefix + command;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        if (!string.IsNullOrEmpty(_workingDirectory))
            process.StartInfo.WorkingDirectory = _workingDirectory;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (!process.Start()) return (false, null);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var succeeded = process.ExitCode == 0;
            var tail = Tail(succeeded ? stdout : stdout + Environment.NewLine + stderr);
            return (succeeded, tail);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (false, "gate timed out");
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    private static string ResolveShell(out string argPrefix)
    {
        var isWindows = OperatingSystem.IsWindows();
        argPrefix = isWindows ? "/C " : "-c ";
        return isWindows ? "cmd.exe" : "/bin/sh";
    }

    private static string? Tail(string text, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[^maxChars..];
    }
}
