using System.Diagnostics;

namespace PiSharp.TsBridge;

internal sealed class SystemBridgeProcessFactory : IBridgeProcessFactory
{
    public IBridgeProcess Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start TypeScript extension bridge.");

        return new SystemBridgeProcess(process);
    }

    private sealed class SystemBridgeProcess(Process process) : IBridgeProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextWriter StandardInput => process.StandardInput;
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
