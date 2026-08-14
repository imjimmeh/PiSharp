using PiSharp.Eval.Kernels;

using PiSharp.Eval.Kernel.CSharp;
using Xunit;

namespace PiSharp.Eval.Tests;

public sealed class ScratchTests
{
    [Fact]
    public async Task DumpScriptErrors()
    {
        await using var kernel = new CSharpKernel();
        await kernel.StartAsync(new KernelStartOptions(Path.GetTempPath()));

        var scripts = new[]
        {
            "Tools.Run(\"bash\", new { command = \"rm -rf /\" })",
            "Tools.Read(\"a.txt\")",
            "int keep = 5; Action<int> f = x => x + 1;",
        };
        foreach (var script in scripts)
        {
            var result = await kernel.ExecuteAsync(script, new KernelExecuteOptions(TimeoutMs: 10000));
            await File.AppendAllTextAsync(Path.Combine(Path.GetTempPath(), "scratch-out.txt"),
                $"=== {script} ===\n{result.Output}\nIsError={result.IsError}\n\n");
        }
        Assert.True(true);
    }
}
