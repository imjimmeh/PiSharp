using PiSharp.Extensions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed record TuiShortcutControllerOptions(
    Func<IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>>> GetExtensionShortcuts,
    IExtensionUi ExtensionUi,
    Action<string> ReportError);

public sealed class TuiShortcutController(TuiShortcutControllerOptions options)
{
    public IReadOnlyList<TuiExtensionShortcutBinding> BuildExtensionShortcutBindings()
    {
        var registrations = options.GetExtensionShortcuts();
        var bindings = new List<TuiExtensionShortcutBinding>();
        foreach (var registration in registrations)
        {
            if (!TuiShortcutKeyParser.TryParse(registration.Value.Keys, out var terminalKeys))
            {
                options.ReportError($"Invalid extension shortcut '{registration.Value.Keys}' from {registration.SourceId}: unsupported key string.");
                continue;
            }

            var conflictingAction = terminalKeys
                .Select(key => TuiShortcutRegistrar.TryResolveGlobalAction(key, out var action) ? action : (TuiShortcutAction?)null)
                .FirstOrDefault(action => action is not null);
            if (conflictingAction is not null)
            {
                options.ReportError($"Extension shortcut '{registration.Value.Keys}' from {registration.SourceId} conflicts with built-in shortcut '{conflictingAction}' and was ignored.");
                continue;
            }

            bindings.Add(new TuiExtensionShortcutBinding(
                registration.SourceId,
                registration.Value.Keys,
                registration.Value.Description,
                terminalKeys,
                token => registration.Value.InvokeAsync(new ExtensionCommandContext(
                    registration.Value.Keys,
                    string.Empty,
                    options.ExtensionUi,
                    UnavailableExtensionSessionApi.Instance,
                    UnavailableExtensionModelApi.Instance,
                    UnavailableExtensionToolApi.Instance,
                    new Dictionary<string, object?>(),
                    token), token)));
        }

        return bindings;
    }

    public bool TryDispatchShortcutKey(
        Key key,
        TuiShortcutDispatcher dispatcher,
        TuiShortcutContext context,
        CancellationToken cancellationToken = default)
        => TuiShortcutRegistrar.TryDispatchShortcutKey(
            key,
            dispatcher,
            context,
            BuildExtensionShortcutBindings,
            options.ReportError,
            cancellationToken);
}
