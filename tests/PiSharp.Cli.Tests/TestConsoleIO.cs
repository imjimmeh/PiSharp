using PiSharp.Cli.IO;

namespace PiSharp.Cli.Tests;

internal sealed class TestConsoleIO : IConsoleIO
{
    private TextWriter _out;

    public TestConsoleIO(string input = "", bool isInputRedirected = false)
    {
        In = new StringReader(input);
        Output = new StringWriter();
        ErrorOutput = new StringWriter();
        _out = Output;
        IsInputRedirected = isInputRedirected;
    }

    public StringWriter Output { get; }
    public StringWriter ErrorOutput { get; }
    public TextReader In { get; }
    public TextWriter Out => _out;
    public TextWriter Error => ErrorOutput;
    public bool IsInputRedirected { get; }
    public bool IsOutputRedirected => false;
    public void SetOut(TextWriter writer) => _out = writer;
}
