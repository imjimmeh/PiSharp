using System.Diagnostics;

namespace PiSharp.Plugins.ProtocolJsonRpc.Process;

/// <summary>
/// <see cref="System.Diagnostics.Process"/> wrapper with redirected stdio and
/// <c>UseShellExecute = false</c> (required for redirection on Windows). Mirrors
/// <c>src/PiSharp.TsBridge/SystemBridgeProcessFactory.cs</c>.
/// </summary>
public sealed class SystemServerProcessFactory : IServerProcessFactory
{
    public IServerProcess Start(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start process '{startInfo.FileName}'.");

        return new SystemServerProcess(process);
    }

    private sealed class SystemServerProcess(System.Diagnostics.Process process) : IServerProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;

        public Stream StandardOutput => process.StandardOutput.BaseStream;

        public bool HasExited => process.HasExited;

        public event Action<string?>? StandardErrorReceived;

        public void BeginErrorReadLine()
        {
            process.ErrorDataReceived += (_, args) => StandardErrorReceived?.Invoke(args.Data);
            process.BeginErrorReadLine();
        }

        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

        public void Dispose() => process.Dispose();
    }
}
