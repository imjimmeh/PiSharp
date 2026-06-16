using PiSharp.Cli.IO;
using Xunit;

namespace PiSharp.Cli.Tests.IO;

public sealed class StdoutGuardTests
{
    [Fact]
    public async Task TakeOverRoutesConsoleOutToNullAndKeepsProtocolWriter()
    {
        var console = new TestConsoleIO();
        await using (var guard = StdoutGuard.TakeOver(console))
        {
            await console.Out.WriteLineAsync("human log");
            await guard.WriteJsonLineAsync("{\"ok\":true}");
        }

        Assert.Equal("{\"ok\":true}" + Environment.NewLine, console.Output.ToString());
        await console.Out.WriteLineAsync("restored");
        Assert.Contains("restored", console.Output.ToString());
    }
}
