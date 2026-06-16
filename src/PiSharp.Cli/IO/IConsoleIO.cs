namespace PiSharp.Cli.IO;

public interface IConsoleIO
{
    TextReader In { get; }
    TextWriter Out { get; }
    TextWriter Error { get; }
    bool IsInputRedirected { get; }
    bool IsOutputRedirected { get; }
    void SetOut(TextWriter writer);
}
