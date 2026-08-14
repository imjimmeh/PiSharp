namespace PiSharp.Runtime.Subagents;

/// <summary>
/// Ambient access to the process-wide <see cref="SubagentSessionService"/>. The runtime binder
/// constructs the service once per runtime; plugins (e.g. PiSharp.Subagents) read
/// <see cref="Current"/> to drive child sessions without an extension-API surface change.
/// </summary>
public static class SubagentRuntimeAccess
{
    private static SubagentSessionService? _current;

    /// <summary>The most recently registered subagent service, or null when none is alive.</summary>
    public static SubagentSessionService? Current => _current;

    /// <summary>Registers the service when constructed. Internal: called by the service constructor.</summary>
    internal static void Register(SubagentSessionService service) => _current = service;

    /// <summary>Clears the registration when the owning service is disposed.</summary>
    internal static void Unregister(SubagentSessionService service)
    {
        if (ReferenceEquals(_current, service))
            _current = null;
    }
}
