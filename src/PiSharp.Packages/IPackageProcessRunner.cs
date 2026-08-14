namespace PiSharp.Packages;

public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

public interface IPackageProcessRunner
{
    Task RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a process capturing stdout/stderr and exit code (never throws on non-zero exit).</summary>
    Task<ProcessRunResult> RunCaptureAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default);
}
