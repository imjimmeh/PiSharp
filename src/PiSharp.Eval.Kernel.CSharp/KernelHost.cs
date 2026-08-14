using PiSharp.Eval.Kernels;

namespace PiSharp.Eval.Kernel.CSharp;

/// <summary>
/// Script globals passed to the C# scripting kernel (the <c>globals</c> argument of
/// <c>CSharpScript.RunAsync</c>). Exposes the session working directory, the typed
/// loopback tool helpers, and a log line appended to the eval output.
/// </summary>
public sealed class KernelHost
{
    private readonly Action<string> _log;

    internal KernelHost(string cwd, IKernelToolBridge? toolBridge, Action<string> log)
    {
        Cwd = cwd;
        Tools = new KernelTools(toolBridge);
        _log = log;
    }

    /// <summary>Session working directory the kernel was started with.</summary>
    public string Cwd { get; }

    /// <summary>Typed loopback helpers over the agent's own tools (read/grep/find/ls).</summary>
    public KernelTools Tools { get; }

    /// <summary>Appends a line to the eval output of the current execution.</summary>
    public void Log(string message) => _log(message ?? string.Empty);
}
