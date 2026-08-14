using System.Diagnostics;

namespace PiSharp.Plugins.ProtocolJsonRpc.Process;

/// <summary>
/// A long-lived child process with byte-accurate stdio streams. Content-Length framing
/// needs raw bytes, so unlike the TsBridge's TextReader/TextWriter wrapper these streams
/// are the un-decoded <see cref="Stream"/>s.
/// </summary>
public interface IServerProcess : IDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    bool HasExited { get; }

    event Action<string?>? StandardErrorReceived;

    void BeginErrorReadLine();

    void Kill(bool entireProcessTree);
}

/// <summary>
/// Testability seam: production uses <see cref="SystemServerProcessFactory"/>; tests inject
/// in-memory stream pairs with a scripted JSON-RPC responder.
/// </summary>
public interface IServerProcessFactory
{
    IServerProcess Start(ProcessStartInfo startInfo);
}
