namespace PiSharp.TsBridge;

internal interface IBridgeProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextWriter StandardInput { get; }
    bool HasExited { get; }
    event Action<string?>? StandardErrorReceived;

    void BeginErrorReadLine();

    void Kill(bool entireProcessTree);
}
