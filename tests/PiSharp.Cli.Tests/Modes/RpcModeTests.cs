using System.Text.Json;
using PiSharp.Ai;
using PiSharp.Agent.Resources;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Serialization;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Parsing;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class RpcModeTests
{
    [Fact]
    public async Task PromptWritesResponseThenFlatSerializedSessionEvents()
    {
        var runtime = await ModeTestRuntime.CreateAsync(ModeTestRuntime.FakeStream("rpc text"));
        var console = new TestConsoleIO("{\"type\":\"prompt\",\"id\":\"1\",\"message\":\"hello\"}" + Environment.NewLine);

        var exitCode = await RpcMode.RunAsync(runtime, console);

        Assert.Equal(0, exitCode);
        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);
        using var response = JsonDocument.Parse(lines[0]);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("prompt", response.RootElement.GetProperty("command").GetString());
        Assert.Contains(lines.Skip(1), line => line.Contains("\"type\":\"message_end\""));
        Assert.DoesNotContain(lines.Skip(1), line => line.Contains("\"type\":\"core\"") || line.Contains("\"type\":\"own\""));
        Assert.DoesNotContain(lines.Skip(1), line => line.Contains("\"event\":"));
    }

    [Fact]
    public async Task PromptForwardsImagesToHarnessEvents()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var input = "{\"type\":\"prompt\",\"id\":\"1\",\"message\":\"hello\",\"images\":[{\"mediaType\":\"image/png\",\"data\":\"abc\"}]}" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        Assert.Contains("image/png", console.Output.ToString());
    }

    [Fact]
    public async Task SetSessionNameEmitsFlatSessionInfoChangedEvent()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_session_name\",\"id\":\"n\",\"name\":\"named\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("\"type\":\"session_info_changed\"") && line.Contains("\"name\":\"named\""));
        Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"own\""));
    }

    [Fact]
    public async Task ExtensionUiResponseAcknowledgesPendingRequest()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"extension_ui_response\",\"id\":\"ui\",\"requestId\":\"missing\",\"cancelled\":true}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.Contains("\"success\":true", console.Output.ToString());
    }

    [Fact]
    public async Task GetStateUsesTypeScriptCompatibleFields()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"get_state\",\"id\":\"s\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        using var response = JsonDocument.Parse(console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]);
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("test", data.GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal(runtime.Session.Metadata.Id, data.GetProperty("sessionId").GetString());
        Assert.False(data.GetProperty("isCompacting").GetBoolean());
    }

    [Fact]
    public async Task SetModelReadsTypeScriptModelIdField()
    {
        var model = PublicApi.Models.First().Descriptor;
        var runtime = await ModeTestRuntime.CreateAsync();
        var input = AgentJsonSerializer.Serialize(new { type = "set_model", id = "m", provider = model.Provider, modelId = model.Id }) + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_model\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(model.Id, runtime.Harness.Model.Id);
    }

    [Fact]
    public async Task GetCommandsIncludesSkillCommandsWithJavaScriptCompatibleSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-commands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skillPath = Path.Combine(root, "skills", "research", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        await File.WriteAllTextAsync(skillPath, "skill content");
        var skill = new Skill("research", "Research skill", "content", skillPath);
        var promptPath = Path.Combine(root, "release.md");
        await File.WriteAllTextAsync(promptPath, "---\ndescription: Release prompt\n---\nRelease {{1}}");
        var args = new CliArgs(PromptTemplates: [promptPath]);
        var runtime = await ModeTestRuntime.CreateAsync(args: args, cwd: root, skills: [skill]);
        var console = new TestConsoleIO("{\"type\":\"get_commands\",\"id\":\"commands\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        using var response = JsonDocument.Parse(lines.First(line => line.Contains("\"command\":\"get_commands\"")));
        var commands = response.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(commands, command => command.GetProperty("name").GetString() == "skill:research" && command.GetProperty("source").GetString() == "skill");
        Assert.Contains(commands, command => command.GetProperty("name").GetString() == "prompt:release" && command.GetProperty("source").GetString() == "prompt");
    }

    [Fact]
    public async Task BashRpcReturnsSuccessWithoutReservedError()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var input = """{"type":"bash","id":"1","command":"ls -la","excludeFromContext":false}""" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"bash\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.False(response.RootElement.TryGetProperty("data", out var _), "Expected no data field when no handler matched");
    }

    [Fact]
    public async Task BashRpcWithHandlerReturnsResult()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.UserBash, (evt, _) =>
        {
            var payload = Assert.IsType<ExtensionUserBashPayload>(evt.Payload);
            evt.SetUserBashResult(result: new ExtensionBashResult(payload.Command, 0, "handled output", ""));
            return Task.CompletedTask;
        });
        var runtime = await ModeTestRuntime.CreateAsync(extensionManager: new ExtensionManager(registry));
        var input = """{"type":"bash","id":"1","command":"my-command","excludeFromContext":false}""" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"bash\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("my-command", data.GetProperty("command").GetString());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        Assert.Equal("handled output", data.GetProperty("output").GetString());
    }

    [Fact]
    public async Task BashRpcWithOperationsHandlerReturnsOperations()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.UserBash, (evt, _) =>
        {
            evt.SetUserBashResult(operations: new ExtensionBashOperations(Command: "rewritten", Cwd: "work", Timeout: 3, ExcludeFromContext: true));
            return Task.CompletedTask;
        });
        var runtime = await ModeTestRuntime.CreateAsync(extensionManager: new ExtensionManager(registry));
        var input = """{"type":"bash","id":"1","command":"my-command","excludeFromContext":false}""" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"bash\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        var operations = response.RootElement.GetProperty("data").GetProperty("operations");
        Assert.Equal("rewritten", operations.GetProperty("command").GetString());
        Assert.Equal("work", operations.GetProperty("cwd").GetString());
        Assert.Equal(3, operations.GetProperty("timeout").GetDouble());
        Assert.True(operations.GetProperty("excludeFromContext").GetBoolean());
    }

    [Fact]
    public async Task AbortBashReturnsFriendlyMessage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var input = """{"type":"abort_bash","id":"1"}""" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"abort_bash\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("No active bash operation to abort.", data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetForkMessagesReturnsEntriesWithIdParentIdAndRole()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        await runtime.Session.AppendMessageAsync(AgentMessages.User("hello"), CancellationToken.None);
        var input = "{\"type\":\"get_fork_messages\",\"id\":\"fm\"}" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"get_fork_messages\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        var data = response.RootElement.GetProperty("data");
        var entries = data.GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1);
        foreach (var entry in entries.EnumerateArray())
        {
            Assert.NotNull(entry.GetProperty("id").GetString());
        }
        var messageEntry = Assert.Single(entries.EnumerateArray(), e => e.TryGetProperty("role", out var role) && role.GetString() == "user");
        Assert.Equal("user", messageEntry.GetProperty("role").GetString());
    }

    [Fact]
    public async Task SetAutoCompaction_enables_auto_compaction()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_auto_compaction\",\"id\":\"sc\",\"enabled\":true}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.True(runtime.AutoCompactionEnabled);
        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_auto_compaction\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.True(response.RootElement.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean());
    }

    [Fact]
    public async Task SetAutoCompaction_disables_auto_compaction()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        runtime.AutoCompactionEnabled = true;
        var console = new TestConsoleIO("{\"type\":\"set_auto_compaction\",\"id\":\"sc\",\"enabled\":false}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.False(runtime.AutoCompactionEnabled);
    }

    [Fact]
    public async Task SetAutoRetry_enables_auto_retry()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_auto_retry\",\"id\":\"sr\",\"enabled\":true}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.True(runtime.AutoRetryEnabled);
        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_auto_retry\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.True(response.RootElement.GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean());
    }

    [Fact]
    public async Task SetAutoRetry_disables_auto_retry()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        runtime.AutoRetryEnabled = true;
        var console = new TestConsoleIO("{\"type\":\"set_auto_retry\",\"id\":\"sr\",\"enabled\":false}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.False(runtime.AutoRetryEnabled);
    }

    [Fact]
    public async Task AbortRetry_resets_retry_state()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        runtime.AutoRetryEnabled = true;
        var console = new TestConsoleIO("{\"type\":\"abort_retry\",\"id\":\"ar\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.False(runtime.AutoRetryEnabled);
        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"abort_retry\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task SetSteeringMode_updates_steering_mode()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_steering_mode\",\"id\":\"sm\",\"mode\":\"one-at-a-time\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.Equal("one-at-a-time", runtime.SteeringMode);
        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_steering_mode\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("one-at-a-time", data.GetProperty("steeringMode").GetString());
    }

    [Fact]
    public async Task SetFollowUpMode_updates_follow_up_mode()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_follow_up_mode\",\"id\":\"fm\",\"mode\":\"one-at-a-time\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        Assert.Equal("one-at-a-time", runtime.FollowUpMode);
        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_follow_up_mode\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("one-at-a-time", data.GetProperty("followUpMode").GetString());
    }

    [Fact]
    public async Task SetSteeringMode_rejects_invalid_mode()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_steering_mode\",\"id\":\"sm\",\"mode\":\"invalid\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_steering_mode\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task SetFollowUpMode_rejects_invalid_mode()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("{\"type\":\"set_follow_up_mode\",\"id\":\"fm\",\"mode\":\"invalid\"}" + Environment.NewLine);

        await RpcMode.RunAsync(runtime, console);

        var lines = console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = lines.First(line => line.Contains("\"command\":\"set_follow_up_mode\""));
        using var response = JsonDocument.Parse(responseLine);
        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
    }
}
