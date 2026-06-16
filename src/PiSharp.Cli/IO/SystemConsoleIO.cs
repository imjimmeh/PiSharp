namespace PiSharp.Cli.IO;

public sealed class SystemConsoleIO : IConsoleIO
{
    public TextReader In => Console.In;
    public TextWriter Out => Console.Out;
    public TextWriter Error => Console.Error;
    public bool IsInputRedirected => Console.IsInputRedirected;
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    public void SetOut(TextWriter writer) => Console.SetOut(writer);
}
