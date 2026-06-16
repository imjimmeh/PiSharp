namespace PiSharp.Cli.IO;

public sealed class StdoutGuard : IAsyncDisposable, IDisposable
{
    private readonly IConsoleIO _console;
    private readonly TextWriter _originalOut;
    private bool _disposed;

    private StdoutGuard(IConsoleIO console, TextWriter originalOut)
    {
        _console = console;
        _originalOut = originalOut;
        ProtocolOut = originalOut;
        _console.SetOut(TextWriter.Null);
    }

    public TextWriter ProtocolOut { get; }

    public static StdoutGuard TakeOver(IConsoleIO console) => new(console, console.Out);

    public Task WriteJsonLineAsync(string jsonLine) => ProtocolOut.WriteLineAsync(jsonLine);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _console.SetOut(_originalOut);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ProtocolOut.FlushAsync();
        Dispose();
    }
}
