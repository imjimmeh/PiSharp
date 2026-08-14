using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.DeclarativeTools.Tests.Fakes;
using Xunit;

namespace PiSharp.DeclarativeTools.Tests;

public sealed partial class ScriptToolExecutorTests
{
    private static ToolDefinition ScriptTool(string name, string scriptPath, TimeSpan? timeout = null, bool allowNonZeroExit = false)
        => new(
            Name: name,
            Label: name,
            Description: "desc",
            ParametersSchema: default,
            PromptSnippet: null,
            PromptGuidelines: [],
            ExecutionMode: null,
            RendererName: null,
            Override: PiSharp.Extensions.ExtensionOverridePolicy.Reject,
            ScriptPath: scriptPath,
            Timeout: timeout,
            AllowNonZeroExit: allowNonZeroExit,
            SourcePath: scriptPath);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(FakeExecutionEnv Env, AgentToolResult<object?> Result)> RunAsync(
        ToolDefinition tool,
        JsonElement parameters,
        TimeSpan? defaultTimeout = null,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        var env = new FakeExecutionEnv("/repo");
        var execute = ScriptToolExecutor.Create(env, tool, defaultTimeout);
        var result = await execute("call-1", parameters, CancellationToken.None, onUpdate);
        return (env, result);
    }

    [Fact]
    public async Task BashTool_CommandUsesBashWithStdinRedirection()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("hello from script");

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);
        var result = await execute("call-1", Args("""{"a":1,"b":"x"}"""), CancellationToken.None, null);

        var (command, options) = Assert.Single(env.ExecutedCommands);
        Assert.Matches(CommandPattern(), command);

        var argsMatch = ArgsFilePattern().Match(command);
        Assert.True(argsMatch.Success);
        // The args file is deleted after execution, so verify its content from the write history.
        var written = Assert.Single(env.WrittenFiles);
        Assert.Equal(argsMatch.Groups["file"].Value, written.Path);
        Assert.Equal("""{"a":1,"b":"x"}""", written.Content);

        Assert.Equal("foo", options.Environment!["PISHARP_TOOL_NAME"]);
        Assert.Equal("/repo", options.Environment["PISHARP_TOOL_CWD"]);
        Assert.Equal("""{"a":1,"b":"x"}""", options.Environment["PISHARP_TOOL_ARGS"]);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("hello from script", text.Text);
    }

    [Fact]
    public async Task ArgsJson_IsAlwaysCompact()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("foo", "/tools/foo.py");
        var execute = ScriptToolExecutor.Create(env, tool, null);
        await execute("call-1", Args("""{ "a" : 1, "b" : "x y" }"""), CancellationToken.None, null);

        var (_, options) = Assert.Single(env.ExecutedCommands);
        Assert.Equal("""{"a":1,"b":"x y"}""", options.Environment!["PISHARP_TOOL_ARGS"]);
    }

    [Fact]
    public async Task UndefinedParameters_UseEmptyObject()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);
        await execute("call-1", default, CancellationToken.None, null);

        var (_, options) = Assert.Single(env.ExecutedCommands);
        Assert.Equal("{}", options.Environment!["PISHARP_TOOL_ARGS"]);
    }

    [Fact]
    public async Task PyOnWindows_UsesPyInterpreter()
    {
        if (!OperatingSystem.IsWindows()) return;

        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("fetch", "/tools/fetch.py");
        var execute = ScriptToolExecutor.Create(env, tool, null);
        await execute("call-1", Args("{}"), CancellationToken.None, null);

        var (command, _) = Assert.Single(env.ExecutedCommands);
        Assert.StartsWith("py \"", command);
    }

    [Fact]
    public async Task NonZeroExit_ThrowsWithOutputAndStatus()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("some output", exitCode: 2);

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execute("call-1", Args("{}"), CancellationToken.None, null));
        Assert.Contains("some output", exception.Message);
        Assert.Contains("Command exited with code 2", exception.Message);
    }

    [Fact]
    public async Task AllowNonZeroExit_ReturnsWithStatusNote()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("grep output", exitCode: 1);

        var tool = ScriptTool("grep-like", "/tools/grep.sh", allowNonZeroExit: true);
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var result = await execute("call-1", Args("{}"), CancellationToken.None, null);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("grep output", text.Text);
        Assert.Contains("Command exited with code 1", text.Text);
    }

    [Fact]
    public async Task TimeoutError_SurfacesTimeoutStatus()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellError(ExecutionErrorCode.Timeout, "Command timed out.");

        var tool = ScriptTool("foo", "/tools/foo.sh", timeout: TimeSpan.FromSeconds(30));
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execute("call-1", Args("{}"), CancellationToken.None, null));
        Assert.Contains("timed out after 30 seconds", exception.Message);
    }

    [Fact]
    public async Task AbortedError_SurfacesAbortStatus()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellError(ExecutionErrorCode.Aborted, "Command aborted.");

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execute("call-1", Args("{}"), CancellationToken.None, null));
        Assert.Contains("Command aborted", exception.Message);
    }

    [Fact]
    public async Task SpawnError_SurfacesMessage()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellError(ExecutionErrorCode.SpawnError, "bash was not found");

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execute("call-1", Args("{}"), CancellationToken.None, null));
        Assert.Contains("bash was not found", exception.Message);
    }

    [Fact]
    public async Task TimeoutOption_IsPassedThrough()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("foo", "/tools/foo.sh", timeout: TimeSpan.FromSeconds(45));
        var execute = ScriptToolExecutor.Create(env, tool, null);
        await execute("call-1", Args("{}"), CancellationToken.None, null);

        var (_, options) = Assert.Single(env.ExecutedCommands);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
    }

    [Fact]
    public async Task DefaultTimeout_AppliedWhenToolHasNone()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, TimeSpan.FromSeconds(10));
        await execute("call-1", Args("{}"), CancellationToken.None, null);

        var (_, options) = Assert.Single(env.ExecutedCommands);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Timeout);
    }

    [Fact]
    public async Task ToolTimeout_WinsOverDefault()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("foo", "/tools/foo.sh", timeout: TimeSpan.FromSeconds(5));
        var execute = ScriptToolExecutor.Create(env, tool, TimeSpan.FromSeconds(10));
        await execute("call-1", Args("{}"), CancellationToken.None, null);

        var (_, options) = Assert.Single(env.ExecutedCommands);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Timeout);
    }

    [Fact]
    public async Task Truncation_SpillsToTempFileAndReturnsDetails()
    {
        var env = new FakeExecutionEnv("/repo");
        var big = new string('x', 60 * 1024);
        env.EnqueueShellResult(big);

        var tool = ScriptTool("big", "/tools/big.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var result = await execute("call-1", Args("{}"), CancellationToken.None, null);

        var details = Assert.IsType<ScriptToolDetails>(result.Details);
        Assert.NotNull(details.FullOutputPath);
        Assert.True(details.Truncation!.Truncated);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("Showing last", text.Text);
        // The full output must have spilled to the fake temp file.
        Assert.NotNull(env.ReadFileOrDefault(details.FullOutputPath!));
    }

    [Fact]
    public async Task StreamingUpdates_AreEmittedThrottled()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueStreamingResult("part one ", "part two ", "part three");

        var updates = new List<AgentToolResult<object?>>();
        var tool = ScriptTool("stream", "/tools/stream.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);

        await execute("call-1", Args("{}"), CancellationToken.None, update => updates.Add(update));

        // Initial empty update + throttled partials (100 ms apart) + final result.
        Assert.True(updates.Count >= 2, $"expected streaming updates, got {updates.Count}");
        Assert.Contains(updates, u => u.Content.Count == 0);
        Assert.Contains(updates, u => u.Content.Count == 1 && ((TextContent)u.Content[0]).Text.Contains("part one"));
        Assert.Contains(updates, u => u.Content.Count == 1 && ((TextContent)u.Content[0]).Text.Contains("part three"));
    }

    [Fact]
    public async Task ArgsTempFile_IsRemovedAfterExecution()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("ok");

        var tool = ScriptTool("foo", "/tools/foo.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);
        await execute("call-1", Args("""{"q":1}"""), CancellationToken.None, null);

        var (command, _) = Assert.Single(env.ExecutedCommands);
        var match = ArgsFilePattern().Match(command);
        Assert.True(match.Success);
        Assert.Null(env.ReadFileOrDefault(match.Groups["file"].Value));
    }

    [Fact]
    public async Task EmptyOutput_ReturnsNoOutputPlaceholder()
    {
        var env = new FakeExecutionEnv("/repo");
        env.EnqueueShellResult("");

        var tool = ScriptTool("quiet", "/tools/quiet.sh");
        var execute = ScriptToolExecutor.Create(env, tool, null);

        var result = await execute("call-1", Args("{}"), CancellationToken.None, null);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("(no output)", text.Text);
    }

    [GeneratedRegex("^bash \"(?<script>.*)\\.sh\" < \"(?<file>.*pi-tool-args-.*\\.json)\"$")]
    private static partial Regex CommandPattern();

    [GeneratedRegex("\"(?<file>/tmp/pi-tool-args-.*\\.json)\"")]
    private static partial Regex ArgsFilePattern();
}
