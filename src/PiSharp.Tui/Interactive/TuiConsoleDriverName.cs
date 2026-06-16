using System.Text;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

internal static class TuiConsoleDriverName
{
    public const string NetDriver = "v2net";

    public static string DefaultForCurrentPlatform()
        => DefaultForPlatform(OperatingSystem.IsWindows());

    public static string DefaultForPlatform(bool isWindows)
        => isWindows ? NetDriver : string.Empty;

    public static void PrepareConsoleForDriver(string driverName)
        => PrepareConsoleForDriver(driverName, () => Console.OutputEncoding, encoding => Console.OutputEncoding = encoding);

    internal static void PrepareConsoleForDriver(string driverName, Func<Encoding> getOutputEncoding, Action<Encoding> setOutputEncoding)
    {
        if (!string.Equals(driverName, NetDriver, StringComparison.Ordinal)) return;
        if (getOutputEncoding().CodePage == Encoding.UTF8.CodePage) return;
        setOutputEncoding(Encoding.UTF8);
    }
}
