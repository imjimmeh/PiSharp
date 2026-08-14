using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-advisor", Name = "PiSharp Advisor", Version = "0.1.0", Description = "A cheap second model that reviews each turn and emits advisor notes.")]

namespace PiSharp.Advisor;

/// <summary>
/// <c>pisharp-advisor</c> extension entry. Wires the advisor watchdog: reads
/// <c>advisor.*</c> settings, subscribes to <c>context</c>/<c>turn_end</c>,
/// owns an <see cref="AdvisorWorker"/>, and registers the <c>/advisor</c> and
/// <c>/advisor-note</c> slash commands. Off by default.
/// </summary>
public sealed class AdvisorExtension : IExtension, IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private IExtensionApi? _api;
    private AdvisorSettings? _settings;
    private AdvisorWorker? _worker;
    private AdvisorTranscript? _transcript;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _settings = new AdvisorSettings(api.Settings);

        var options = _settings.Read();
        _transcript = new AdvisorTranscript(options.MaxTranscriptTurns);
        _worker = new AdvisorWorker(
            api.Completion,
            api.Descriptor.EffectiveSourceId,
            _transcript,
            OnAdvisorEvent);
        _worker.Configure(options, options.Model);

        _subscriptions.Add(api.On(ExtensionEventNames.Context, OnContextAsync));
        _subscriptions.Add(api.On(ExtensionEventNames.TurnEnd, OnTurnEndAsync));
        _subscriptions.Add(api.Settings.OnChange(OnSettingsChanged));

        api.RegisterCommand(new ExtensionCommandRegistration(
            "advisor",
            "Toggle the advisor on/off and list recent advisor notes. Usage: /advisor [on|off]",
            OnAdvisorCommandAsync));
        api.RegisterCommand(new ExtensionCommandRegistration(
            "advisor-note",
            "Toggle the advisor on/off and list recent advisor notes.",
            OnAdvisorCommandAsync));
    }

    public bool Enabled => _worker?.Enabled ?? false;

    private Task OnContextAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (_worker is not null && evt.Payload is AgentHarnessOwnEvent.Context ctx)
        {
            _worker.SetContext(ctx.Messages);
        }
        return Task.CompletedTask;
    }

    private Task OnTurnEndAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (_worker is not null && evt.Payload is AgentEvent.TurnEnd turnEnd)
        {
            var turnId = (turnEnd.Message as AssistantMessage)?.ResponseId ?? string.Empty;
            _worker.AppendTurn(turnEnd.Message);
            _worker.OnTurnEnd(turnId);
        }
        return Task.CompletedTask;
    }

    private void OnSettingsChanged(ExtensionSettingsChange change)
    {
        if (_settings is null || _worker is null) return;
        var options = _settings.Read();
        _worker.Configure(options, options.Model);
    }

    private Task OnAdvisorCommandAsync(string args, CancellationToken cancellationToken)
    {
        var api = _api;
        var worker = _worker;
        if (api is null || worker is null) return Task.CompletedTask;

        var trimmed = args.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "on":
            case "enable":
                return ToggleAsync(api, worker, true, cancellationToken);
            case "off":
            case "disable":
                return ToggleAsync(api, worker, false, cancellationToken);
            default:
                // Bare "/advisor" reports the current state, then lists recent notes.
                _ = api.SendMessageAsync(AgentMessages.User(worker.Enabled ? "Advisor is ON." : "Advisor is OFF."), cancellationToken);
                return ListNotesAsync(api, worker, cancellationToken);
        }
    }

    private static async Task ToggleAsync(IExtensionApi api, AdvisorWorker worker, bool enable, CancellationToken cancellationToken)
    {
        await api.Settings.SetAsync("enabled", enable, ExtensionSettingsScope.Source, cancellationToken).ConfigureAwait(false);
        // Settings OnChange re-applies the new options to the worker.
        await api.SendMessageAsync(
            AgentMessages.User(enable ? "Advisor enabled." : "Advisor disabled."),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ListNotesAsync(IExtensionApi api, AdvisorWorker worker, CancellationToken cancellationToken)
    {
        var notes = worker.RecentNotes;
        if (notes.Count == 0)
        {
            await api.SendMessageAsync(AgentMessages.User("No advisor notes yet."), cancellationToken).ConfigureAwait(false);
            return;
        }

        var lines = notes.Select(n => $"[{n.Kind}] {n.Text}");
        await api.SendMessageAsync(AgentMessages.User("Recent advisor notes:\n" + string.Join("\n", lines)), cancellationToken).ConfigureAwait(false);
    }

    private void OnAdvisorEvent(ExtensionAdvisorEvent evt)
    {
        var api = _api;
        if (api is null) return;

        // Live signal for other plugins and (when wired) the daemon/client stream.
        _ = api.Events.EmitAsync(ExtensionEventNames.AdvisorNote, evt, CancellationToken.None);

        // In-process durability (best-effort; the live event is the primary contract).
        try
        {
            _ = api.Session.AppendEntryAsync("advisor_note", evt.Note, CancellationToken.None);
        }
        catch
        {
            // Non-fatal — the advisor is best-effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _worker?.Dispose();
        _worker = null;
    }
}
