using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Eval;
using PiSharp.Extensions;

namespace PiSharp.Eval.Tests;

/// <summary>
/// In-process harness for the eval extension, built on the real <see cref="ExtensionManager"/>
/// + <see cref="ExtensionRuntimeBinding"/> contracts (the same path the runtime uses to host
/// native extensions). Callers wire the loopback and completion delegates.
/// </summary>
internal sealed class EvalTestHost : IAsyncDisposable
{
    private readonly ExtensionManager _manager;
    private readonly EvalExtension _extension;
    private readonly ExtensionRuntimeBinding _binding;

    public ExtensionRegistry Registry => _manager.Registry;
    public List<(string EventName, object? Payload)> EmittedEvents { get; }
    public List<AgentMessage> SentMessages { get; }
    public string Cwd { get; }

    private EvalTestHost(
        ExtensionManager manager,
        EvalExtension extension,
        ExtensionRuntimeBinding binding,
        string cwd,
        List<(string EventName, object? Payload)> emittedEvents,
        List<AgentMessage> sentMessages)
    {
        _manager = manager;
        _extension = extension;
        _binding = binding;
        Cwd = cwd;
        EmittedEvents = emittedEvents;
        SentMessages = sentMessages;
    }

    public static async Task<EvalTestHost> CreateAsync(
        string cwd,
        Func<string, JsonElement, CancellationToken, Task<AgentToolResult<object?>>>? executeToolByName = null,
        Func<IReadOnlyList<AgentMessage>?, CancellationToken, Task<ExtensionCompletionResult>>? complete = null,
        string? sessionName = "test-session")
    {
        var registry = new ExtensionRegistry();
        var events = new List<(string EventName, object? Payload)>();
        var sent = new List<AgentMessage>();
        var binding = new ExtensionRuntimeBinding(cwd, hasUi: false, NoExtensionUi.Instance)
        {
            GetSessionNameAsync = _ => Task.FromResult<string?>(sessionName),
            SendMessageAsync = (message, _, _, _) =>
            {
                sent.Add(message);
                return Task.CompletedTask;
            },
            EmitEventAsync = (name, payload, _) =>
            {
                events.Add((name, payload));
                return Task.CompletedTask;
            },
        };
        var host = new EvalTestHost(new ExtensionManager(registry), new EvalExtension(), binding, cwd, events, sent);

        if (executeToolByName is not null)
        {
            binding.ExecuteToolByNameAsync = executeToolByName;
        }
        else
        {
            // Default: resolve the tool from the registry (the runtime's own pattern).
            binding.ExecuteToolByNameAsync = (name, parameters, ct) =>
            {
                var tool = registry.Tools.FirstOrDefault(t => t.Value.Name == name)?.Value;
                return tool is null
                    ? Task.FromResult(new AgentToolResult<object?>([new TextContent($"Tool '{name}' was not found.")], null))
                    : tool.ExecuteAsync($"eval-loopback:{name}", parameters, ct);
            };
        }

        if (complete is not null)
        {
            binding.CompleteAsync = (_, _, messages, _, _, _, ct) => complete(messages, ct);
        }

        await host._manager.InitializeAsync(
            new ExtensionDescriptor("test.eval", "Test Eval", "1.0.0", typeof(EvalTestHost).Assembly.Location),
            host._extension,
            binding);

        return host;
    }

    public IAgentTool? FindTool(string name)
        => Registry.Tools.FirstOrDefault(t => t.Value.Name == name)?.Value;

    public async Task<AgentToolResult<object?>> RunToolAsync(string name, object? parameters, CancellationToken ct = default)
    {
        var tool = FindTool(name) ?? throw new InvalidOperationException($"Tool '{name}' is not registered.");
        var json = parameters is null
            ? default
            : JsonSerializer.SerializeToElement(parameters, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return await tool.ExecuteAsync($"test:{name}", json, ct);
    }

    public async Task<string> RunCommandAsync(string name, string args, CancellationToken ct = default)
    {
        var registration = Registry.Commands.FirstOrDefault(c => c.Value.Name == name)?.Value
            ?? throw new InvalidOperationException($"Command '/{name}' is not registered.");
        var context = new ExtensionCommandContext(
            name, args, NoExtensionUi.Instance, _binding.Session, _binding.Model, _binding.Tools,
            new Dictionary<string, object?>(), ct);
        await registration.InvokeAsync(context, ct);
        return SentMessages.LastOrDefault() switch
        {
            UserMessage user => string.Concat(user.Content.OfType<TextContent>().Select(c => c.Text)),
            _ => string.Empty,
        };
    }

    public Task FireEventAsync(string eventName, object? payload = null, CancellationToken ct = default)
    {
        var evt = new ExtensionEvent(eventName, null!, payload);
        var handlers = Registry.HandlersFor(eventName);
        return Task.WhenAll(handlers.Select(h => h.Value.Handler(evt, ct)));
    }

    public ValueTask DisposeAsync()
    {
        return _extension is IAsyncDisposable disposable
            ? disposable.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
