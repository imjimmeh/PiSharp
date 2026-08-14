using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Tui.Interactive;

namespace PiSharp.Tui.Tests;

/// <summary>
/// Test implementation of <see cref="ITuiRuntimeFacade"/> that wraps the in-process
/// <see cref="AgentHarness{TMetadata}"/> produced by <see cref="TuiIntegrationTestHost"/>.
/// </summary>
internal sealed class TestRuntimeFacade(AgentHarness<JsonlSessionMetadata> harness) : ITuiRuntimeFacade
{
    public AgentHarnessPhase Phase => harness.Phase;
    public ModelDescriptor Model => harness.Model;
    public ThinkingLevel ThinkingLevel => harness.ThinkingLevel;
    public IReadOnlyList<string> ActiveToolNames => harness.ActiveToolNames;
    public Func<CancellationToken, Task>? OnHarnessReplaced { get; set; }

    public IDisposable Subscribe(Func<AgentHarnessEvent, CancellationToken, Task> listener)
        => harness.Subscribe(listener);

    public void Abort() => harness.Abort();

    public Task PromptAsync(string text, IReadOnlyList<ImageContent> images, CancellationToken token)
        => harness.PromptAsync(text, images, token);

    public void Steer(AgentMessage message) => harness.Steer(message);
}
