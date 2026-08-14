using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Eval;
using PiSharp.Eval.Bench;
using PiSharp.Eval.Events;
using PiSharp.Eval.Kernel.CSharp;
using PiSharp.Eval.Kernels;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// In-process harness integration tests: the eval tool (registration + persistent state
/// across turns), loopback through the wired execute-by-name delegate, lifecycle hooks,
/// snapshot on shutdown, and a /bench round-trip with a scripted completion.
/// </summary>
public sealed class EvalExtensionIntegrationTests
{
    public EvalExtensionIntegrationTests()
    {
        EvalKernelRegistry.Clear();
        EvalKernelRegistry.RegisterFactory(new CSharpKernelFactory());
    }

    private static string NewCwd()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"eval-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string OutputOf(AgentToolResult<object?> result)
        => string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));

    [Fact]
    public async Task Initialize_RegistersToolCommandsAndLifecycleHooks()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(cwd);
        try
        {
            Assert.NotNull(host.FindTool("eval"));

            var commandNames = host.Registry.Commands.Select(c => c.Value.Name).ToArray();
            Assert.Contains("kernel", commandNames);
            Assert.Contains("bench", commandNames);

            var handlerEvents = host.Registry.Handlers.Select(h => h.Value.EventName).ToArray();
            Assert.Contains(ExtensionEventNames.SessionBeforeCompact, handlerEvents);
            Assert.Contains(ExtensionEventNames.CompactionStart, handlerEvents);
            Assert.Contains(ExtensionEventNames.SessionBeforeFork, handlerEvents);
            Assert.Contains(ExtensionEventNames.SessionShutdown, handlerEvents);
            Assert.Contains(ExtensionEventNames.SavePoint, handlerEvents);
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task EvalTool_TwoCallsShareKernelState()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(cwd);
        try
        {
            var first = await host.RunToolAsync("eval", new { code = "int answer = 42;" });
            Assert.DoesNotContain("Error", OutputOf(first));

            var second = await host.RunToolAsync("eval", new { code = "answer * 2" });
            var text = OutputOf(second);
            Assert.DoesNotContain("Error", text);
            Assert.Contains("84", text);
            Assert.Contains("\"kernel\": \"csharp\"", text);
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task EvalTool_ResetFlag_ClearsState()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(cwd);
        try
        {
            await host.RunToolAsync("eval", new { code = "int answer = 42;" });

            var reset = await host.RunToolAsync("eval", new { code = "int answer = 7;", reset = true });
            Assert.Contains("\"wasReset\": true", OutputOf(reset));

            var check = await host.RunToolAsync("eval", new { code = "answer" });
            Assert.DoesNotContain("Error", OutputOf(check));
            Assert.Contains("7", OutputOf(check));
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task Loopback_KernelCallsAgentToolThroughBridge()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(
            cwd,
            executeToolByName: (name, parameters, _) => name == "read"
                ? Task.FromResult(new AgentToolResult<object?>([new TextContent($"canned:{parameters.GetProperty("path").GetString()}")], null))
                : Task.FromResult(new AgentToolResult<object?>([new TextContent("Error: nope")], null)));
        try
        {
            var result = await host.RunToolAsync("eval", new { code = "Tools.Read(\"a.txt\")" });
            var text = OutputOf(result);
            Assert.DoesNotContain("Error", text);
            Assert.Contains("canned:a.txt", text);

            // Loopback emits only eval_loopback_* events — never tool_call/tool_execution_*.
            var loopbackEvents = host.EmittedEvents.Where(e => e.EventName.StartsWith("eval_loopback_", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, loopbackEvents.Length);
            Assert.Equal(EvalEventNames.LoopbackToolCall, loopbackEvents[0].EventName);
            Assert.Equal(EvalEventNames.LoopbackToolResult, loopbackEvents[1].EventName);
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task Loopback_BlockedTool_ReturnsErrorString()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(
            cwd,
            executeToolByName: (_, _, _) => Task.FromException<AgentToolResult<object?>>(
                new InvalidOperationException("must not be invoked")));
        try
        {
            var result = await host.RunToolAsync("eval", new { code = "Tools.Run(\"bash\", new { command = \"rm -rf /\" })" });
            var text = OutputOf(result);
            Assert.Contains("not allowed", text);
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task CompactionHooks_SnapshotKernels()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(cwd);
        try
        {
            await host.RunToolAsync("eval", new { code = "int x = 42;" });

            await host.FireEventAsync(ExtensionEventNames.SessionBeforeCompact, new { reason = "test" });

            // The snapshot event was emitted by the compaction hook.
            Assert.Contains(host.EmittedEvents, e => e.EventName == EvalEventNames.Snapshot);

            // Kernel state is intact after compaction.
            var result = await host.RunToolAsync("eval", new { code = "x" });
            Assert.Contains("42", OutputOf(result));
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task SessionShutdown_DisposesKernels()
    {
        var cwd = NewCwd();
        var host = await EvalTestHost.CreateAsync(cwd);
        try
        {
            await host.RunToolAsync("eval", new { code = "int x = 1;" });

            await host.FireEventAsync(ExtensionEventNames.SessionShutdown, new { reason = "dispose" });
            await host.DisposeAsync();
        }
        finally
        {
            // Registry disposal already happened via the shutdown hook; DisposeAsync is
            // idempotent (guarded by _disposed).
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task KernelCommand_RoundTrip()
    {
        var cwd = NewCwd();
        await using var host = await EvalTestHost.CreateAsync(cwd);
        try
        {
            await host.RunToolAsync("eval", new { code = "int x = 42;" });

            var status = await host.RunCommandAsync("kernel", "");
            Assert.Contains("csharp", status);
            Assert.Contains("running: True", status);
            Assert.Contains("variables: 1", status);

            var reset = await host.RunCommandAsync("kernel", "reset");
            Assert.Contains("reset", reset);

            var snapshot = await host.RunCommandAsync("kernel", "snapshot");
            Assert.Contains("snapshot saved", snapshot);
            Assert.Contains(host.EmittedEvents, e => e.EventName == EvalEventNames.Snapshot);

            var restore = await host.RunCommandAsync("kernel", "restore");
            Assert.Contains("restored", restore);
            Assert.Contains(host.EmittedEvents, e => e.EventName == EvalEventNames.Restore);
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task BenchCommand_RoundTrip_ProducesSummaryAndJsonl()
    {
        var cwd = NewCwd();
        var benchDir = Path.Combine(cwd, ".pi", "bench");
        Directory.CreateDirectory(benchDir);
        await File.WriteAllTextAsync(Path.Combine(benchDir, "bench.json"), """
            {
              "name": "integration",
              "model": "fake/model",
              "runs": 1,
              "cases": [
                { "name": "pass", "prompt": "Say world", "expected": { "text": "world" } },
                { "name": "fail", "prompt": "Say nope", "expected": { "text": "missing" } }
              ]
            }
            """);

        await using var host = await EvalTestHost.CreateAsync(
            cwd,
            complete: (messages, _) =>
            {
                var prompt = string.Concat((messages ?? []).OfType<UserMessage>().SelectMany(m => m.Content.OfType<TextContent>()).Select(c => c.Text));
                var text = prompt.Contains("world") ? "world" : "something else";
                return Task.FromResult(new ExtensionCompletionResult(
                    ExtensionCompletionStatus.Ok, text, null,
                    new UsageInfo(Input: 10, Output: 20, TotalTokens: 30, Cost: new UsageCost(Total: 0.001m))));
            });
        try
        {
            var summary = await host.RunCommandAsync("bench", "");

            Assert.Contains("Bench 'integration'", summary);
            Assert.Contains("1/2 passed", summary);

            // JSONL result file under <benchDir>/runs/.
            var runsDir = Path.Combine(benchDir, "runs");
            var files = Directory.GetFiles(runsDir, "*.jsonl");
            Assert.Single(files);
            var lines = (await File.ReadAllLinesAsync(files[0])).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);
            var first = JsonSerializer.Deserialize<BenchCaseResult>(lines[0], BenchSpecParser.SpecJsonOptions);
            Assert.NotNull(first);
            Assert.True(first.Passed);
            Assert.Equal(30, first.TotalTokens);

            // bench_* events streamed.
            var eventNames = host.EmittedEvents.Select(e => e.EventName).ToArray();
            Assert.Contains(EvalEventNames.BenchRunStart, eventNames);
            Assert.Contains(EvalEventNames.BenchCaseEnd, eventNames);
            Assert.Contains(EvalEventNames.BenchRunEnd, eventNames);

            var runEnd = host.EmittedEvents.First(e => e.EventName == EvalEventNames.BenchRunEnd).Payload as BenchRunEndEvent;
            Assert.NotNull(runEnd);
            Assert.Equal(1, runEnd.Passed);
            Assert.Equal(2, runEnd.Total);
        }
        finally
        {
            if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true);
        }
    }
}
