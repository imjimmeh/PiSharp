using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Coordination;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;
using System.Text.Json;
using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class CoordinationExtensionTests
{
    [Fact]
    public async Task InitializeRegistersCoordinationToolsAndMiddleware()
    {
        await using var fixture = await ExtensionTestFixture
            .Create(new CoordinationExtension())
            .WithCwd("/repo")
            .BuildAsync();

        Assert.Contains(fixture.Registry.Tools, tool => tool.Value.Name == "coordination_roster");
        Assert.Contains(fixture.Registry.Tools, tool => tool.Value.Name == "coordination_send");
        Assert.Contains(fixture.Registry.Tools, tool => tool.Value.Name == "coordination_inbox");
        Assert.NotEmpty(fixture.Registry.Middleware);
    }

    [Fact]
    public async Task InitializeRegistersCoordinationToolParameterSchemas()
    {
        await using var fixture = await ExtensionTestFixture
            .Create(new CoordinationExtension())
            .WithCwd("/repo")
            .BuildAsync();

        var sendSchema = fixture.Registry.Tools.First(tool => tool.Value.Name == "coordination_send").Value.ParametersSchema;
        Assert.Equal("object", sendSchema.GetProperty("type").GetString());
        Assert.Contains(sendSchema.GetProperty("required").EnumerateArray(), item => item.GetString() == "body");
        Assert.Equal("string", sendSchema.GetProperty("properties").GetProperty("to").GetProperty("type").GetString());
        Assert.Equal("string", sendSchema.GetProperty("properties").GetProperty("body").GetProperty("type").GetString());

        var inboxSchema = fixture.Registry.Tools.First(tool => tool.Value.Name == "coordination_inbox").Value.ParametersSchema;
        Assert.Equal("boolean", inboxSchema.GetProperty("properties").GetProperty("includeRead").GetProperty("type").GetString());
        Assert.Equal("integer", inboxSchema.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ExtensionRegistersAgentThroughConnector()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);

        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        await using var daemon = await CoordinationDaemon.StartAsync(metadataDirectory, pipeName);

        var lease = new CoordinationDaemonLease(
            Environment.ProcessId, pipeName, repo,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var metadataPath = Path.Combine(metadataDirectory, "daemon.json");
        var tempPath = metadataPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(lease, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(tempPath, metadataPath, overwrite: true);

        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var descriptor = new ExtensionDescriptor(
            Id: "pisharp.coordination",
            Name: "PiSharp Coordination",
            Version: "0.1.0",
            Path: typeof(CoordinationExtension).Assembly.Location);

        await manager.InitializeAsync(
            descriptor,
            new CoordinationExtension(),
            new ExtensionRuntimeActions(repo, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

        var client = new CoordinationClient(daemon.Endpoint);
        var roster = await client.GetRosterAsync();
        Assert.Contains(roster.Agents, a => a.Cwd == repo);
    }

    [Fact]
    public async Task ExtensionRegistersRepositoryRootWhenStartedFromSubdirectory()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);
        var subdirectory = Path.Combine(repo, "src", "feature");
        Directory.CreateDirectory(subdirectory);

        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        await using var daemon = await CoordinationDaemon.StartAsync(metadataDirectory, pipeName);

        var lease = new CoordinationDaemonLease(
            Environment.ProcessId, pipeName, repo,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var metadataPath = Path.Combine(metadataDirectory, "daemon.json");
        var tempPath = metadataPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(lease, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(tempPath, metadataPath, overwrite: true);

        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var descriptor = new ExtensionDescriptor(
            Id: "pisharp.coordination",
            Name: "PiSharp Coordination",
            Version: "0.1.0",
            Path: typeof(CoordinationExtension).Assembly.Location);

        await manager.InitializeAsync(
            descriptor,
            new CoordinationExtension(),
            new ExtensionRuntimeActions(subdirectory, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

        var client = new CoordinationClient(daemon.Endpoint);
        var roster = await client.GetRosterAsync();
        Assert.Contains(roster.Agents, a => a.Cwd == repo);
        Assert.DoesNotContain(roster.Agents, a => a.Cwd == subdirectory);
    }

    [Fact]
    public async Task ExtensionDisposesOwnedDaemonOnCancellation()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var descriptor = new ExtensionDescriptor(
            Id: "pisharp.coordination",
            Name: "PiSharp Coordination",
            Version: "0.1.0",
            Path: typeof(CoordinationExtension).Assembly.Location);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.InitializeAsync(
                descriptor,
                new CoordinationExtension(),
                new ExtensionRuntimeActions(repo, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask),
                cts.Token));

        var metadataPath = Path.Combine(metadataDirectory, "daemon.json");
        Assert.True(File.Exists(metadataPath), "Metadata file should exist after connector started daemon.");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        var pipeName = doc.RootElement.GetProperty("pipeName").GetString()!;

        var client = new CoordinationClient(new CoordinationEndpoint(pipeName));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetRosterAsync());
    }

    [Fact]
    public async Task SendToolDeliversMessageToInboxTool()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var senderFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            await using var receiverFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = senderFixture.GetTool("coordination_send");
            var sendArgs = JsonDocument.Parse("""{"to":"all","body":"status?"}""").RootElement;
            var sendResult = await sendTool.ExecuteAsync("call-1", sendArgs);
            var sendText = ((TextContent)sendResult.Content[0]).Text;

            var inboxTool = receiverFixture.GetTool("coordination_inbox");
            var inboxArgs = JsonDocument.Parse("{}").RootElement;
            var inboxResult = await inboxTool.ExecuteAsync("call-2", inboxArgs);
            var inboxText = ((TextContent)inboxResult.Content[0]).Text;

            Assert.Contains("status?", inboxText);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task RosterToolReturnsAgentEntries()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var rosterTool = fixture.GetTool("coordination_roster");
            var args = JsonDocument.Parse("{}").RootElement;
            var result = await rosterTool.ExecuteAsync("call-1", args);
            var text = ((TextContent)result.Content[0]).Text;

            Assert.Contains("Coordination Roster", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SendToolRejectsEmptyBody()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = fixture.GetTool("coordination_send");
            var args = JsonDocument.Parse("""{"to":"agent-x","body":""}""").RootElement;
            var result = await sendTool.ExecuteAsync("call-1", args);
            var text = ((TextContent)result.Content[0]).Text;

            Assert.Contains("body", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SendToolRejectsEmptyTo()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = fixture.GetTool("coordination_send");
            var args = JsonDocument.Parse("""{"to":"","body":"hello"}""").RootElement;
            var result = await sendTool.ExecuteAsync("call-1", args);
            var text = ((TextContent)result.Content[0]).Text;

            Assert.Contains("to", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SendToolRejectsOversizedBody()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = fixture.GetTool("coordination_send");
            var bigBody = new string('x', 8193);
            var args = JsonDocument.Parse($$"""{"to":"agent-x","body":"{{bigBody}}"}""").RootElement;
            var result = await sendTool.ExecuteAsync("call-1", args);
            var text = ((TextContent)result.Content[0]).Text;

            Assert.Contains("8192", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task InboxToolOmitsMessagesAlreadyReadByDefault()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var senderFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            await using var receiverFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = senderFixture.GetTool("coordination_send");
            var inboxTool = receiverFixture.GetTool("coordination_inbox");

            await sendTool.ExecuteAsync("c-1", JsonDocument.Parse("""{"to":"all","body":"first"}""").RootElement);

            var inboxDefault = JsonDocument.Parse("{}").RootElement;
            var firstRead = await inboxTool.ExecuteAsync("c-2", inboxDefault);
            var firstText = ((TextContent)firstRead.Content[0]).Text;
            Assert.Contains("first", firstText);

            var secondRead = await inboxTool.ExecuteAsync("c-3", inboxDefault);
            var secondText = ((TextContent)secondRead.Content[0]).Text;
            Assert.DoesNotContain("first", secondText);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task InboxToolCanIncludeReadMessages()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var senderFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            await using var receiverFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = senderFixture.GetTool("coordination_send");
            var inboxTool = receiverFixture.GetTool("coordination_inbox");

            await sendTool.ExecuteAsync("c-1", JsonDocument.Parse("""{"to":"all","body":"include-me"}""").RootElement);

            await inboxTool.ExecuteAsync("c-2", JsonDocument.Parse("{}").RootElement);

            var includeReadResult = await inboxTool.ExecuteAsync("c-3", JsonDocument.Parse("""{"includeRead":true}""").RootElement);
            var includeReadText = ((TextContent)includeReadResult.Content[0]).Text;
            Assert.Contains("include-me", includeReadText);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task InboxToolQuotesEveryBodyLine()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var senderFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            await using var receiverFixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = senderFixture.GetTool("coordination_send");
            var inboxTool = receiverFixture.GetTool("coordination_inbox");

            var multilineBody = "line1\nline2\nline3";
            var escapedBody = multilineBody.Replace("\n", "\\n");
            await sendTool.ExecuteAsync("c-1", JsonDocument.Parse($$"""{"to":"all","body":"{{escapedBody}}"}""").RootElement);

            var inboxResult = await inboxTool.ExecuteAsync("c-2", JsonDocument.Parse("{}").RootElement);
            var inboxText = ((TextContent)inboxResult.Content[0]).Text;

            Assert.Contains("> line1", inboxText);
            Assert.Contains("> line2", inboxText);
            Assert.Contains("> line3", inboxText);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SendToolRejectsNonStringTo()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var sendTool = fixture.GetTool("coordination_send");
            var args = JsonDocument.Parse("""{"to":123,"body":"hello"}""").RootElement;
            var result = await sendTool.ExecuteAsync("call-1", args);
            var text = ((TextContent)result.Content[0]).Text;

            Assert.Contains("to", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task AllToolsReturnDiagnosticWhenNotConnected()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tempFile, string.Empty);
        try
        {
            var registry = new ExtensionRegistry();
            var manager = new ExtensionManager(registry);
            var descriptor = new ExtensionDescriptor(
                Id: "pisharp.coordination",
                Name: "PiSharp Coordination",
                Version: "0.1.0",
                Path: typeof(CoordinationExtension).Assembly.Location);

            await manager.InitializeAsync(
                descriptor,
                new CoordinationExtension(),
                new ExtensionRuntimeActions(tempFile, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

            var emptyArgs = JsonDocument.Parse("{}").RootElement;
            var rosterTool = registry.Tools.First(t => t.Value.Name == "coordination_roster").Value;
            var sendTool = registry.Tools.First(t => t.Value.Name == "coordination_send").Value;
            var inboxTool = registry.Tools.First(t => t.Value.Name == "coordination_inbox").Value;

            var rosterText = ((TextContent)(await rosterTool.ExecuteAsync("c-1", emptyArgs)).Content[0]).Text;
            var sendText = ((TextContent)(await sendTool.ExecuteAsync("c-2", JsonDocument.Parse("""{"to":"x","body":"hi"}""").RootElement)).Content[0]).Text;
            var inboxText = ((TextContent)(await inboxTool.ExecuteAsync("c-3", emptyArgs)).Content[0]).Text;

            Assert.Contains("not connected", rosterText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not connected", sendText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not connected", inboxText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ToolsReturnDiagnosticWhenNotConnected()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tempFile, string.Empty);
        try
        {
            var registry = new ExtensionRegistry();
            var manager = new ExtensionManager(registry);
            var descriptor = new ExtensionDescriptor(
                Id: "pisharp.coordination",
                Name: "PiSharp Coordination",
                Version: "0.1.0",
                Path: typeof(CoordinationExtension).Assembly.Location);

            await manager.InitializeAsync(
                descriptor,
                new CoordinationExtension(),
                new ExtensionRuntimeActions(tempFile, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

            var inboxTool = registry.Tools.First(t => t.Value.Name == "coordination_inbox").Value;
            var args = JsonDocument.Parse("{}").RootElement;
            var result = await inboxTool.ExecuteAsync("call-1", args);
            var text = ((TextContent)result.Content[0]).Text;

            Assert.Contains("not connected", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task BeforePromptRenderAppendsCoordinationBrief()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            var seedClient = new CoordinationClient(daemon.Endpoint);
            var otherAgentId = "other-" + Guid.NewGuid().ToString("N")[..8];
            await seedClient.RegisterAgentAsync(
                new AgentRegistration(otherAgentId, Environment.ProcessId, null, null, repo));
            await seedClient.SendMessageAsync(
                Guid.NewGuid().ToString("N"), otherAgentId, "all", "alert from other agent");

            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var handlers = fixture.Registry.HandlersFor(ExtensionEventNames.BeforePromptRender);
            Assert.NotEmpty(handlers);

            var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforePromptRender(
                Prompt: "test prompt",
                Images: [],
                CompositionContext: new SystemPromptCompositionContext(
                    Cwd: repo,
                    CurrentDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    Mode: PromptMode.Default,
                    Tools: [],
                    SelectedToolNames: [],
                    ExplicitGuidelines: [],
                    CustomPrompt: null,
                    AppendPrompt: null,
                    ContextFiles: [],
                    Skills: [],
                    DocumentationPaths: new PromptDocumentationPaths("README.md", "docs", "examples")),
                Document: new SystemPromptDocument([], []),
                Resources: new { }));

            var extensionEvent = ExtensionEventMapper.Map(harnessEvent);
            foreach (var registration in handlers)
            {
                await registration.Value.Handler(extensionEvent, CancellationToken.None);
            }

            Assert.NotNull(extensionEvent.ModifiedPromptDocumentPatch);
            var patch = extensionEvent.ModifiedPromptDocumentPatch!;
            Assert.NotNull(patch.AppendSections);
            Assert.Contains(patch.AppendSections!, section => section.Id == CoordinationBriefFormatter.BriefSectionId);
            var briefSection = patch.AppendSections!.First(s => s.Id == CoordinationBriefFormatter.BriefSectionId);
            Assert.Contains("Known Agents", briefSection.Content, StringComparison.Ordinal);
            Assert.Contains(otherAgentId, briefSection.Content, StringComparison.Ordinal);
            Assert.Contains("Unread Messages", briefSection.Content, StringComparison.Ordinal);
            Assert.Contains("alert from other agent", briefSection.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("# Coordination Brief", briefSection.Content, StringComparison.Ordinal);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task BeforePromptRenderNoOpWhenDaemonUnavailable()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tempFile, string.Empty);
        try
        {
            var registry = new ExtensionRegistry();
            var manager = new ExtensionManager(registry);
            var descriptor = new ExtensionDescriptor(
                Id: "pisharp.coordination",
                Name: "PiSharp Coordination",
                Version: "0.1.0",
                Path: typeof(CoordinationExtension).Assembly.Location);

            await manager.InitializeAsync(
                descriptor,
                new CoordinationExtension(),
                new ExtensionRuntimeActions(tempFile, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

            var handlers = registry.HandlersFor(ExtensionEventNames.BeforePromptRender);
            Assert.NotEmpty(handlers);

            var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforePromptRender(
                Prompt: "test prompt",
                Images: [],
                CompositionContext: new SystemPromptCompositionContext(
                    Cwd: tempFile,
                    CurrentDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    Mode: PromptMode.Default,
                    Tools: [],
                    SelectedToolNames: [],
                    ExplicitGuidelines: [],
                    CustomPrompt: null,
                    AppendPrompt: null,
                    ContextFiles: [],
                    Skills: [],
                    DocumentationPaths: new PromptDocumentationPaths("README.md", "docs", "examples")),
                Document: new SystemPromptDocument([], []),
                Resources: new { }));

            var extensionEvent = ExtensionEventMapper.Map(harnessEvent);
            foreach (var registration in handlers)
            {
                await registration.Value.Handler(extensionEvent, CancellationToken.None);
            }

            Assert.Null(extensionEvent.ModifiedPromptDocumentPatch);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MiddlewareRecordsFileReadToDaemonState()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var middlewares = fixture.Registry.Middleware;
            Assert.Single(middlewares);

            await fixture.RunBeforeMiddlewareAsync("read", JsonDocument.Parse("""{"filePath":"notes.md"}""").RootElement);

            Assert.Contains(daemon.State.RecentReads,
                r => r.Path == "notes.md");
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task MiddlewareRecordsFileWriteOnlyInAfterStage()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var middlewares = fixture.Registry.Middleware;
            Assert.Single(middlewares);

            await fixture.RunBeforeMiddlewareAsync("write", JsonDocument.Parse("""{"filePath":"output.txt"}""").RootElement);

            Assert.DoesNotContain(daemon.State.RecentWrites,
                w => w.Path == "output.txt");

            await fixture.RunAfterMiddlewareAsync("write", JsonDocument.Parse("""{"filePath":"output.txt"}""").RootElement, isError: false);

            Assert.Contains(daemon.State.RecentWrites,
                w => w.Path == "output.txt");
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task MiddlewareNoOpsForBashAndUnknownTools()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var middlewares = fixture.Registry.Middleware;
            Assert.Single(middlewares);

            var beforeReads = daemon.State.RecentReads.Count;
            var beforeWrites = daemon.State.RecentWrites.Count;

            await fixture.RunBeforeMiddlewareAsync("bash", JsonDocument.Parse("""{"command":"cat notes.md"}""").RootElement);

            Assert.Equal(beforeReads, daemon.State.RecentReads.Count);
            Assert.Equal(beforeWrites, daemon.State.RecentWrites.Count);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsLifetimeAndDisposesConnection()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            var extension = new CoordinationExtension();
            var registry = new ExtensionRegistry();
            var manager = new ExtensionManager(registry);
            var descriptor = new ExtensionDescriptor(
                Id: "pisharp.coordination",
                Name: "PiSharp Coordination",
                Version: "0.1.0",
                Path: typeof(CoordinationExtension).Assembly.Location);

            await manager.InitializeAsync(
                descriptor,
                extension,
                new ExtensionRuntimeActions(repo, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

            Assert.False(extension.IsLifetimeCancelled, "Lifetime should not be cancelled after init.");
            Assert.False(extension.IsConnectionDisposed, "Connection should be alive after init.");

            var handlers = registry.HandlersFor(ExtensionEventNames.SessionShutdown);
            Assert.NotEmpty(handlers);

            var harnessEvent = new AgentHarnessEvent.Own(
                new AgentHarnessOwnEvent.SessionShutdown("dispose"));
            var extensionEvent = ExtensionEventMapper.Map(harnessEvent);

            foreach (var registration in handlers)
                await registration.Value.Handler(extensionEvent, CancellationToken.None);

            Assert.True(extension.IsLifetimeCancelled, "Lifetime should be cancelled after session shutdown.");
            Assert.True(extension.IsConnectionDisposed, "Connection should be disposed after session shutdown.");
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SessionShutdownCleanupIsIdempotent()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            var extension = new CoordinationExtension();
            var registry = new ExtensionRegistry();
            var manager = new ExtensionManager(registry);
            var descriptor = new ExtensionDescriptor(
                Id: "pisharp.coordination",
                Name: "PiSharp Coordination",
                Version: "0.1.0",
                Path: typeof(CoordinationExtension).Assembly.Location);

            await manager.InitializeAsync(
                descriptor,
                extension,
                new ExtensionRuntimeActions(repo, false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

            var handlers = registry.HandlersFor(ExtensionEventNames.SessionShutdown);
            Assert.NotEmpty(handlers);

            var harnessEvent = new AgentHarnessEvent.Own(
                new AgentHarnessOwnEvent.SessionShutdown("dispose"));
            var extensionEvent = ExtensionEventMapper.Map(harnessEvent);

            foreach (var registration in handlers)
                await registration.Value.Handler(extensionEvent, CancellationToken.None);

            foreach (var registration in handlers)
                await registration.Value.Handler(extensionEvent, CancellationToken.None);

            Assert.True(extension.IsLifetimeCancelled);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task BeforeStageWritePreflightsButDoesNotRecord()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            var seedClient = new CoordinationClient(daemon.Endpoint);
            var otherAgentId = "agent-b-" + Guid.NewGuid().ToString("N")[..8];
            await seedClient.RegisterAgentAsync(
                new AgentRegistration(otherAgentId, Environment.ProcessId, null, null, repo));
            await seedClient.RecordFileReadAsync(otherAgentId, "shared.md");

            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var mainAgentId = daemon.State.Agents.Keys.First(k => k != otherAgentId);
            await seedClient.RecordFileReadAsync(mainAgentId, "shared.md");
            await seedClient.RecordFileWriteAsync(otherAgentId, "shared.md");

            var beforeWrites = daemon.State.RecentWrites.Count;
            var middlewares = fixture.Registry.Middleware;
            var nextCalled = false;

            var context = MiddlewareContextBuilder.Before("write", JsonDocument.Parse("""{"filePath":"shared.md"}""").RootElement);

            foreach (var mw in middlewares)
                await mw.Value(context, (_, _) => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

            Assert.True(context.Blocked, "Middleware should block when daemon reports conflict.");
            Assert.NotNull(context.BlockReason);
            Assert.Contains("re-read", context.BlockReason);
            Assert.Contains(otherAgentId, context.BlockReason);
            Assert.False(nextCalled, "Next should not be called when blocked.");
            Assert.Equal(beforeWrites, daemon.State.RecentWrites.Count);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task ReadRecordedSynchronouslyBeforeNext()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var middlewares = fixture.Registry.Middleware;
            var readCalled = false;
            var recorded = false;

            var context = MiddlewareContextBuilder.Before("read", JsonDocument.Parse("""{"filePath":"sync-test.md"}""").RootElement);

            foreach (var mw in middlewares)
                await mw.Value(context, async (ctx, ct) =>
                {
                    readCalled = true;
                    recorded = daemon.State.RecentReads.Any(r => r.Path == "sync-test.md");
                }, CancellationToken.None);

            Assert.True(readCalled, "next should have been called.");
            Assert.True(recorded, "Read should be recorded before next is called.");
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task AfterStageFailedWriteNotRecorded()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var beforeWrites = daemon.State.RecentWrites.Count;

            await fixture.RunAfterMiddlewareAsync("write", JsonDocument.Parse("""{"filePath":"failed.txt"}""").RootElement, isError: true);

            Assert.Equal(beforeWrites, daemon.State.RecentWrites.Count);
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task AfterStageSuccessfulWriteRecorded()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            await fixture.RunAfterMiddlewareAsync("write", JsonDocument.Parse("""{"filePath":"success.txt"}""").RootElement, isError: false);

            Assert.True(daemon.State.RecentWrites.Any(w => w.Path == "success.txt"),
                "Write should be recorded after successful after-stage execution.");
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task MiddlewareNoOpsWhenDaemonUnavailable()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tempFile, string.Empty);
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(tempFile)
                .BuildAsync();

            var middlewares = fixture.Registry.Middleware;
            Assert.Single(middlewares);

            var exception = await Record.ExceptionAsync(async () =>
            {
                await fixture.RunBeforeMiddlewareAsync("read", JsonDocument.Parse("""{"filePath":"ghost.md"}""").RootElement);
            });

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }


    private static async Task<(string Repo, Action RepoCleanup, CoordinationDaemon Daemon)> CreateDaemonWithLeaseAsync()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);

        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        var daemon = await CoordinationDaemon.StartAsync(metadataDirectory, pipeName);

        var lease = new CoordinationDaemonLease(
            Environment.ProcessId, pipeName, repo,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var metadataPath = Path.Combine(metadataDirectory, "daemon.json");
        var tempPath = metadataPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(lease, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(tempPath, metadataPath, overwrite: true);

        return (repo, () =>
        {
            try { Directory.Delete(repo, recursive: true); }
            catch { /* best-effort cleanup */ }
        }, daemon);
    }


    [Fact]
    public async Task SubagentEventHandlerRethrowsCancellation()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var handlers = fixture.Registry.HandlersFor("subagents:started");
            Assert.NotEmpty(handlers);

            var evt = new ExtensionEvent("subagents:started", null!,
                JsonDocument.Parse("""{"id":"sub-ct","type":"Test"}""").RootElement.Clone());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                handlers.First().Value.Handler(evt, cts.Token));
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task BeforePromptRenderRethrowsCancellation()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var handlers = fixture.Registry.HandlersFor(ExtensionEventNames.BeforePromptRender);
            Assert.NotEmpty(handlers);

            var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforePromptRender(
                Prompt: "test prompt",
                Images: [],
                CompositionContext: new SystemPromptCompositionContext(
                    Cwd: repo,
                    CurrentDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    Mode: PromptMode.Default,
                    Tools: [],
                    SelectedToolNames: [],
                    ExplicitGuidelines: [],
                    CustomPrompt: null,
                    AppendPrompt: null,
                    ContextFiles: [],
                    Skills: [],
                    DocumentationPaths: new PromptDocumentationPaths("README.md", "docs", "examples")),
                Document: new SystemPromptDocument([], []),
                Resources: new { }));

            var extensionEvent = ExtensionEventMapper.Map(harnessEvent);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                handlers.First().Value.Handler(extensionEvent, cts.Token));
        }
        finally
        {
            repoCleanup();
        }
    }

    [Fact]
    public async Task SessionShutdownUnregistersAgentFromDaemon()
    {
        var (repo, repoCleanup, daemon) = await CreateDaemonWithLeaseAsync();
        await using var daemonDispose = daemon;
        try
        {
            await using var fixture = await ExtensionTestFixture
                .Create(new CoordinationExtension())
                .WithCwd(repo)
                .BuildAsync();

            var verifyClient = new CoordinationClient(daemon.Endpoint);
            var rosterBefore = await verifyClient.GetRosterAsync();
            Assert.NotEmpty(rosterBefore.Agents);

            await fixture.FireSessionShutdownAsync();

            var rosterAfter = await verifyClient.GetRosterAsync();
            Assert.Empty(rosterAfter.Agents);
        }
        finally
        {
            repoCleanup();
        }
    }

}
