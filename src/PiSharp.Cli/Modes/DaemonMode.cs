using PiSharp.Cli.IO;
using PiSharp.Cli.Parsing;

namespace PiSharp.Cli.Modes;

public static class DaemonMode
{
    public static async Task<int> RunAsync(DaemonCommandArgs command, IConsoleIO console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(console);

        await console.Error.WriteLineAsync("not implemented");
        return 1;
    }
}