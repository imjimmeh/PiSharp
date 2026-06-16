using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Prompt;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiPromptSubmissionCoordinatorTests
{
    private static TuiRenderState EmptyState() => TuiRenderState.Empty(
        "sid", null, new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);

    private static TuiFooterSnapshot EmptyFooterSnapshot() =>
        new("", null, 0, 0, 0, 0, 0m, 0, 0, false, new Dictionary<string, string>());

    [Fact]
    public void CompletePrompt_WithSlashPrefix_ReturnsCommandCompletions()
    {
        var state = EmptyState();
        var appContext = new FakeTuiApplicationContext();
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        using var inlineSelection = new TuiInlineSelectionCoordinator(
            shell.Prompt, () => { }, appContext.Post);
        var gateway = new TuiStateGateway(() => state, s => state = s, renderCoordinator, appContext, CancellationToken.None);
        var sessionContext = new TuiSessionContext { CurrentHarness = TuiIntegrationTestHost.CreateHarness() };
        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null),
            CompleteCommand: text => ["/model", "/tree", "/help"]);

        var coordinator = new TuiPromptSubmissionCoordinator(
            shell, inlineSelection, gateway, sessionContext, options,
            new PromptFileReferenceCompletionProvider(Environment.CurrentDirectory));

        var completions = coordinator.CompletePrompt("/", 1);

        Assert.NotEmpty(completions);
        Assert.All(completions, c => Assert.True(c.Value.StartsWith("/", StringComparison.Ordinal) || c.Label.StartsWith("/", StringComparison.Ordinal)));
    }

    [Fact]
    public void CompletePrompt_WithoutSlashPrefix_DoesNotReturnCommandCompletions()
    {
        var state = EmptyState();
        var appContext = new FakeTuiApplicationContext();
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        using var inlineSelection = new TuiInlineSelectionCoordinator(
            shell.Prompt, () => { }, appContext.Post);
        var gateway = new TuiStateGateway(() => state, s => state = s, renderCoordinator, appContext, CancellationToken.None);
        var sessionContext = new TuiSessionContext { CurrentHarness = TuiIntegrationTestHost.CreateHarness() };
        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null),
            CompleteCommand: _ => ["/model", "/tree"]);

        var coordinator = new TuiPromptSubmissionCoordinator(
            shell, inlineSelection, gateway, sessionContext, options,
            new PromptFileReferenceCompletionProvider(Environment.CurrentDirectory));

        var completions = coordinator.CompletePrompt("hello", 5);

        // No command completions — returns file-reference completions (may be empty for a temp dir)
        Assert.DoesNotContain(completions, c => c.Value == "/model" || c.Value == "/tree");
    }
}
