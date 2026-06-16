using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiSharp.Cli.Packages;

public sealed class SystemProcessRunner : IPackageProcessRunner
{
    public async Task RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var useShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = useShellExecute,
            RedirectStandardOutput = !useShellExecute,
            RedirectStandardError = !useShellExecute
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not find executable '{fileName}' on PATH. {ex.Message}", ex);
        }

        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        try
        {
            if (!useShellExecute)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdout, stderr);
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Process '{fileName} {arguments}' exited with code {process.ExitCode}.");

        process.Dispose();
    }
}
