using System.Diagnostics;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

[CollectionDefinition(nameof(WorkflowSessionsExtensionCollection), DisableParallelization = true)]
public sealed class WorkflowSessionsExtensionCollection { }

[Collection(nameof(WorkflowSessionsExtensionCollection))]
public sealed class WorkflowSessionsExtensionTests
{
    [Fact]
    public async Task WorkflowExtensionLoads()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: root), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadAsync(WorkflowExtensionPath(), binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("pisharp.workflows", result.Descriptor!.ProvidesServices!);
        Assert.Equal("eager", result.Descriptor.Activation);
        var tool = Assert.Single(registry.Tools, item => item.Value.Name == "workflow_run").Value;
        Assert.Equal("Workflow Run", tool.Label);
    }

    [Fact]
    public async Task WorkflowRunToolRunsSingleNodeWithSafeDefaultArgs()
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root);
        using var env = TemporaryEnvironment(fake, stateDir);
        var appendEntries = new List<object>();
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance)
        {
            AppendEntryAsync = (_, data, _) => { appendEntries.Add(data); return Task.CompletedTask; }
        };
        await using var host = await CreateLoadedHostAsync(root, registry, binding);
        using var args = JsonDocument.Parse("{\"workflowId\":\"wf_test\",\"nodeId\":\"node_a\",\"prompt\":\"hello workflow\"}");

        var result = await host.InvokeToolAsync(new TsToolCallRequest(WorkflowExtensionPath(), "workflow_run", args.RootElement.Clone(), "tool-call"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello workflow", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        var invocation = Assert.Single(await ReadInvocationsAsync(root));
        Assert.Contains("--print", invocation.Args);
        Assert.Contains("--no-extensions", invocation.Args);
        Assert.Equal("hello workflow", invocation.Args.Last());
        Assert.True(appendEntries.Count >= 2);
        Assert.NotEmpty(await File.ReadAllTextAsync(Path.Combine(stateDir, "workflow-runs.jsonl")));
    }

    [Fact]
    public async Task WorkflowRunToolReturnsErrorDetailsForFailedNode()
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root);
        using var env = TemporaryEnvironment(fake, stateDir);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = await CreateLoadedHostAsync(root, registry, binding);
        using var args = JsonDocument.Parse("{\"workflowId\":\"wf_test\",\"nodeId\":\"node_fail\",\"prompt\":\"please FAIL\"}");

        var result = await host.InvokeToolAsync(new TsToolCallRequest(WorkflowExtensionPath(), "workflow_run", args.RootElement.Clone(), "tool-call"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("failed", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
        var details = ToJson(result.Details);
        Assert.Equal("Failed", details.GetProperty("status").GetString());
        Assert.Equal(2, details.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("{\"workflowId\":\"wf_bad\",\"nodes\":[{\"id\":\"a\",\"prompt\":\"one\",\"dependsOn\":[\"b\"]}]}", "unknown node")]
    [InlineData("{\"workflowId\":\"wf_bad\",\"nodes\":[{\"id\":\"a\",\"prompt\":\"one\"},{\"id\":\"a\",\"prompt\":\"two\"}]}", "Duplicate workflow node id")]
    [InlineData("{\"workflowId\":\"wf_bad\",\"nodes\":[{\"id\":\"a\",\"prompt\":\"one\",\"dependsOn\":[\"b\"]},{\"id\":\"b\",\"prompt\":\"two\",\"dependsOn\":[\"a\"]}]}", "cycle")]
    public async Task WorkflowRunRejectsInvalidDagBeforeSpawningChildren(string json, string expectedError)
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root);
        using var env = TemporaryEnvironment(fake, stateDir);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = await CreateLoadedHostAsync(root, registry, binding);
        using var args = JsonDocument.Parse(json);

        var result = await host.InvokeToolAsync(new TsToolCallRequest(WorkflowExtensionPath(), "workflow_run", args.RootElement.Clone(), "tool-call"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(expectedError, Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadInvocationsAsync(root));
    }

    [Fact]
    public async Task WorkflowDagRunsDependenciesInOrderAndBlocksFailedDescendants()
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root);
        using var env = TemporaryEnvironment(fake, stateDir);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = await CreateLoadedHostAsync(root, registry, binding);
        using var args = JsonDocument.Parse("""
            {
              "workflowId":"wf_dag",
              "maxConcurrency":2,
              "nodes":[
                {"id":"a","prompt":"first"},
                {"id":"b","prompt":"please FAIL","dependsOn":["a"]},
                {"id":"c","prompt":"never","dependsOn":["b"]}
              ]
            }
            """);

        var result = await host.InvokeToolAsync(new TsToolCallRequest(WorkflowExtensionPath(), "workflow_run", args.RootElement.Clone(), "tool-call"), CancellationToken.None);

        Assert.True(result.IsError);
        var details = ToJson(result.Details);
        var results = details.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(["a", "b", "c"], results.Select(item => item.GetProperty("nodeId").GetString()!).ToArray());
        Assert.Equal("Completed", results[0].GetProperty("status").GetString());
        Assert.Equal("Failed", results[1].GetProperty("status").GetString());
        Assert.Equal("Blocked", results[2].GetProperty("status").GetString());
        Assert.Equal(["first", "please FAIL"], (await ReadInvocationsAsync(root)).Select(item => item.Args.Last()).ToArray());
    }

    [Fact]
    public async Task WorkflowDagHonorsMaxConcurrencyOne()
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root, delayMs: 100);
        using var env = TemporaryEnvironment(fake, stateDir);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = await CreateLoadedHostAsync(root, registry, binding);
        using var args = JsonDocument.Parse("""
            { "workflowId":"wf_serial", "maxConcurrency":1, "nodes":[
              {"id":"a","prompt":"one"}, {"id":"b","prompt":"two"}, {"id":"c","prompt":"three"}
            ]}
            """);

        var result = await host.InvokeToolAsync(new TsToolCallRequest(WorkflowExtensionPath(), "workflow_run", args.RootElement.Clone(), "tool-call"), CancellationToken.None);

        Assert.False(result.IsError);
        var invocations = await ReadInvocationsAsync(root);
        Assert.Equal(3, invocations.Count);
        Assert.True(invocations[1].StartedAt >= invocations[0].CompletedAt);
        Assert.True(invocations[2].StartedAt >= invocations[1].CompletedAt);
    }

    [Fact]
    public async Task WorkflowServiceConsumerCanRunNode()
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root);
        using var env = TemporaryEnvironment(fake, stateDir);
        var consumer = Path.Combine(root, "consumer.mjs");
        await File.WriteAllTextAsync(consumer, """
            export default async function activate(pi) {
              const workflows = await pi.extensions.waitFor('pisharp.workflows', { timeoutMs: 1000 });
              const result = await workflows.runNode({ workflowId: 'wf_service', nodeId: 'node_service', prompt: 'from service' });
              pi.registerCommand(`workflow-service-${result.finalText}`, { description: 'Workflow service result', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: root), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var loaded = await host.LoadManyAsync([WorkflowExtensionPath(), consumer], binding, CancellationToken.None);

        Assert.True(loaded.Ok);
        Assert.All(loaded.Results!, item => Assert.True(item.Ok, item.Error));
        Assert.Contains(registry.Commands, command => command.Value.Name == "workflow-service-from service");
    }

    [Fact]
    public async Task WorkflowMetadataCanBeListedByWorkflowId()
    {
        var root = TempRoot();
        var stateDir = Path.Combine(root, "state");
        var fake = await CreateFakeRunnerAsync(root);
        using var env = TemporaryEnvironment(fake, stateDir);
        var consumer = Path.Combine(root, "metadata-consumer.mjs");
        await File.WriteAllTextAsync(consumer, """
            export default async function activate(pi) {
              const workflows = await pi.extensions.waitFor('pisharp.workflows', { timeoutMs: 1000 });
              await workflows.runNode({ workflowId: 'wf_meta', nodeId: 'node_meta', prompt: 'metadata' });
              const runs = await workflows.listRuns({ workflowId: 'wf_meta' });
              pi.registerCommand(`workflow-metadata-${runs.length > 0}`, { description: 'Workflow metadata', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: root), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var loaded = await host.LoadManyAsync([WorkflowExtensionPath(), consumer], binding, CancellationToken.None);

        Assert.True(loaded.Ok);
        Assert.All(loaded.Results!, item => Assert.True(item.Ok, item.Error));
        Assert.Contains(registry.Commands, command => command.Value.Name == "workflow-metadata-true");
        Assert.True(File.Exists(Path.Combine(stateDir, "workflow-runs.jsonl")));
    }

    private static async Task<TsExtensionHost> CreateLoadedHostAsync(string root, ExtensionRegistry registry, ExtensionRuntimeBinding binding)
    {
        var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: root), registry, binding);
        await host.StartAsync(CancellationToken.None);
        var result = await host.LoadAsync(WorkflowExtensionPath(), binding, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        return host;
    }

    private static async Task<string> CreateFakeRunnerAsync(string root, int delayMs = 0)
    {
        var runner = Path.Combine(root, "fake-runner.mjs");
        await File.WriteAllTextAsync(runner, $$"""
            import fs from 'node:fs';
            const root = {{JsonSerializer.Serialize(root)}};
            const delayMs = {{delayMs}};
            const args = process.argv.slice(2);
            const prompt = args[args.length - 1] ?? '';
            const startedAt = Date.now();
            if (delayMs > 0) await new Promise(resolve => setTimeout(resolve, delayMs));
            const completedAt = Date.now();
            fs.appendFileSync(`${root}/invocations.jsonl`, JSON.stringify({ args, startedAt, completedAt }) + '\n');
            if (prompt.includes('FAIL')) {
              console.error(`failed: ${prompt}`);
              process.exit(2);
            }
            console.log(prompt);
            """);
        if (OperatingSystem.IsWindows())
        {
            var command = Path.Combine(root, "fake-pisharp.cmd");
            await File.WriteAllTextAsync(command, "@echo off\r\nnode \"%~dp0fake-runner.mjs\" %*\r\n");
            return command;
        }
        else
        {
            var command = Path.Combine(root, "fake-pisharp.sh");
            await File.WriteAllTextAsync(command, "#!/usr/bin/env sh\nnode \"$(dirname \"$0\")/fake-runner.mjs\" \"$@\"\n");
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return command;
        }
    }

    private static async Task<IReadOnlyList<FakeInvocation>> ReadInvocationsAsync(string root)
    {
        var file = Path.Combine(root, "invocations.jsonl");
        if (!File.Exists(file)) return [];
        var lines = await File.ReadAllLinesAsync(file);
        return lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonSerializer.Deserialize<FakeInvocation>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToArray();
    }

    private static JsonElement ToJson(object? value)
        => JsonSerializer.SerializeToElement(value);

    private static IDisposable TemporaryEnvironment(string fakeRunner, string stateDir)
    {
        var oldRunner = Environment.GetEnvironmentVariable("PISHARP_WORKFLOW_PISHARP_BIN");
        var oldState = Environment.GetEnvironmentVariable("PISHARP_WORKFLOW_STATE_DIR");
        Environment.SetEnvironmentVariable("PISHARP_WORKFLOW_PISHARP_BIN", fakeRunner);
        Environment.SetEnvironmentVariable("PISHARP_WORKFLOW_STATE_DIR", stateDir);
        return new DisposableAction(() =>
        {
            Environment.SetEnvironmentVariable("PISHARP_WORKFLOW_PISHARP_BIN", oldRunner);
            Environment.SetEnvironmentVariable("PISHARP_WORKFLOW_STATE_DIR", oldState);
        });
    }

    private static string WorkflowExtensionPath()
        => Path.Combine(FindRepositoryRoot(), "extensions", "workflow-sessions");

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-workflow-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root containing PiSharp.sln.");
    }

    private sealed record FakeInvocation(string[] Args, long StartedAt, long CompletedAt);

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
