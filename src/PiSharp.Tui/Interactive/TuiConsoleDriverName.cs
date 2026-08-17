using System.Text;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

internal static class TuiConsoleDriverName
{
    public const string NetDriver = "v2net";
    public const string EnvironmentVariable = "PISHARP_TUI_DRIVER";

    public static string DefaultForCurrentPlatform()
    {
        var overrideDriver = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDriver)) return overrideDriver;
        return DefaultForPlatform(OperatingSystem.IsWindows());
    }

    public static string DefaultForPlatform(bool isWindows)
        => string.Empty;

    public static void PrepareConsoleForDriver(string driverName)
        => PrepareConsoleForDriver(driverName, () => Console.OutputEncoding, encoding => Console.OutputEncoding = encoding);

    internal static void PrepareConsoleForDriver(string driverName, Func<Encoding> getOutputEncoding, Action<Encoding> setOutputEncoding)
    {
        if (getOutputEncoding().CodePage == Encoding.UTF8.CodePage) return;
        setOutputEncoding(Encoding.UTF8);
    }
}
