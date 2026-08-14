using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;

namespace PiSharp.Tui.Interactive;

/// <summary>Runtime surface the TUI host consumes. Local impl wraps SessionRuntime; a remote impl (Phase 3) drives a daemon.</summary>
public interface ITuiRuntimeFacade
{
    AgentHarnessPhase Phase { get; }
    ModelDescriptor Model { get; }
    ThinkingLevel ThinkingLevel { get; }
    IReadOnlyList<string> ActiveToolNames { get; }
    IDisposable Subscribe(Func<AgentHarnessEvent, CancellationToken, Task> listener);
    void Abort();
    Task PromptAsync(string text, IReadOnlyList<ImageContent> images, CancellationToken token);
    void Steer(AgentMessage message);
    Func<CancellationToken, Task>? OnHarnessReplaced { get; set; }
}
