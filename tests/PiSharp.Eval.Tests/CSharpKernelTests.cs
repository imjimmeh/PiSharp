using PiSharp.Eval.Kernel.CSharp;
using PiSharp.Eval.Kernels;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// Unit tests for the in-process Roslyn C# scripting kernel: execution, persistent state,
/// reset semantics, timeout → poisoned reset, compile/runtime errors, and the serialized gate.
/// </summary>
public sealed class CSharpKernelTests
{
    private const string KernelName = "csharp";

    private static async Task<CSharpKernel> StartKernelAsync(string? cwd = null, CancellationToken ct = default)
    {
        var kernel = new CSharpKernel();
        await kernel.StartAsync(new KernelStartOptions(cwd ?? Path.GetTempPath()), ct);
        return kernel;
    }

    [Fact]
    public async Task Execute_ReturnsOutput()
    {
        await using var kernel = await StartKernelAsync();

        var result = await kernel.ExecuteAsync("1 + 1");
        Assert.False(result.IsError);
        Assert.False(result.TimedOut);
        Assert.False(result.WasReset);
        Assert.Contains("2", result.Output);
    }

    [Fact]
    public async Task Execute_ConsoleOutputIsCaptured()
    {
        await using var kernel = await StartKernelAsync();

        var result = await kernel.ExecuteAsync("Console.WriteLine(\"hello kernel\");");
        Assert.False(result.IsError);
        Assert.Contains("hello kernel", result.Output);
    }

    [Fact]
    public async Task Execute_CompileError_IsErrorWithDiagnostics()
    {
        await using var kernel = await StartKernelAsync();

        var result = await kernel.ExecuteAsync("var x = ;");
        Assert.True(result.IsError);
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task Execute_RuntimeException_IsError()
    {
        await using var kernel = await StartKernelAsync();

        var result = await kernel.ExecuteAsync("throw new InvalidOperationException(\"boom\");");
        Assert.True(result.IsError);
        Assert.Contains("boom", result.Output);
    }

    [Fact]
    public async Task Variables_PersistAcrossExecutions()
    {
        await using var kernel = await StartKernelAsync();

        var first = await kernel.ExecuteAsync("int answer = 42; string name = \"pi\";");
        Assert.False(first.IsError);

        var second = await kernel.ExecuteAsync("answer + 1");
        Assert.False(second.IsError);
        Assert.Contains("43", second.Output);

        var third = await kernel.ExecuteAsync("name.ToUpper()");
        Assert.False(third.IsError);
        Assert.Contains("PI", third.Output);
    }

    [Fact]
    public async Task Reset_ClearsState()
    {
        await using var kernel = await StartKernelAsync();

        await kernel.ExecuteAsync("int answer = 42;");
        await kernel.ResetAsync();

        var result = await kernel.ExecuteAsync("answer");
        Assert.True(result.IsError);   // 'answer' no longer defined
        Assert.DoesNotContain("42", result.Output);
    }

    [Fact]
    public async Task Execute_ResetOption_ClearsState()
    {
        await using var kernel = await StartKernelAsync();

        await kernel.ExecuteAsync("int answer = 42;");
        var result = await kernel.ExecuteAsync("int answer = 7;", new KernelExecuteOptions(Reset: true));
        Assert.False(result.IsError);
        Assert.True(result.WasReset);

        var check = await kernel.ExecuteAsync("answer");
        Assert.Contains("7", check.Output);
    }

    [Fact]
    public async Task Timeout_PoisonsKernel_NextCallResets()
    {
        await using var kernel = await StartKernelAsync();

        // Deliberate runaway script that outlives the timeout; 10s bound so the abandoned
        // task terminates on its own instead of spinning a threadpool thread forever.
        var timedOut = await kernel.ExecuteAsync("await Task.Delay(10000)", new KernelExecuteOptions(TimeoutMs: 200));
        Assert.True(timedOut.TimedOut);
        Assert.True(timedOut.IsError);

        var next = await kernel.ExecuteAsync("int alive = 1; alive", new KernelExecuteOptions(TimeoutMs: 5000));
        Assert.True(next.WasReset);
        Assert.False(next.TimedOut);
        Assert.False(next.IsError);
        Assert.Contains("1", next.Output);

        var final = await kernel.ExecuteAsync("alive");
        Assert.False(final.IsError);
        Assert.Contains("1", final.Output);
    }

    [Fact]
    public async Task ConcurrentExecutes_SerializeOnGate()
    {
        await using var kernel = await StartKernelAsync();
        await kernel.ExecuteAsync("int counter = 0;");

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => kernel.ExecuteAsync("counter++;"))
            .ToArray();
        await Task.WhenAll(tasks);

        var result = await kernel.ExecuteAsync("counter");
        Assert.False(result.IsError);
        Assert.Contains("4", result.Output);
    }

    [Fact]
    public async Task Snapshot_RoundTripsVariables()
    {
        await using var kernel = await StartKernelAsync();
        await kernel.ExecuteAsync("int answer = 42; string name = \"pi\"; List<int> nums = new() { 1, 2, 3 };");

        var snapshot = await kernel.SnapshotAsync();
        Assert.False(snapshot.Lossy);
        Assert.Equal("csharp", snapshot.KernelName);
        Assert.Equal(3, snapshot.Variables.Count);

        await using var fresh = await StartKernelAsync();
        await fresh.RestoreAsync(snapshot);

        var answer = await fresh.ExecuteAsync("answer");
        Assert.Contains("42", answer.Output);

        var nums = await fresh.ExecuteAsync("nums.Sum()");
        Assert.Contains("6", nums.Output);
    }

    [Fact]
    public async Task Snapshot_LossyValue_RestoresAsNullWithWarning()
    {
        await using var kernel = await StartKernelAsync();
        // A delegate is not JSON-serializable → lossy variable.
        await kernel.ExecuteAsync("int keep = 5; Func<int,int> g = x => x + 1;");

        var snapshot = await kernel.SnapshotAsync();
        Assert.True(snapshot.Lossy);
        Assert.Contains(snapshot.Variables, v => v.Name == "g" && v.Lossy && v.Json is null);

        await using var fresh = await StartKernelAsync();
        await fresh.RestoreAsync(snapshot);

        // Restore warning surfaces on the next execution's output (the first one drains it).
        var keep = await fresh.ExecuteAsync("keep");
        Assert.False(keep.IsError);
        Assert.Contains("5", keep.Output);
        Assert.Contains("lossy", keep.Output);
    }

    [Fact]
    public async Task Log_AppendsToOutput()
    {
        await using var kernel = await StartKernelAsync();

        var result = await kernel.ExecuteAsync("Log(\"trace line\");");
        Assert.False(result.IsError);
        Assert.Contains("trace line", result.Output);
    }

    [Fact]
    public async Task Cwd_IsExposedToScript()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "eval-cwd-test");
        Directory.CreateDirectory(cwd);
        try
        {
            await using var kernel = await StartKernelAsync(cwd);
            var result = await kernel.ExecuteAsync("Cwd");
            Assert.False(result.IsError);
            Assert.Contains(cwd, result.Output);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }
}
