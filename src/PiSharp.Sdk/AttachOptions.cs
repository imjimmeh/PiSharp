namespace PiSharp.Sdk;


/// <summary>
/// Options for <see cref="PiSharpClient.AttachAsync"/>: where to start replaying the session's
/// retained event log and how to handle headless UI requests.
/// <param name="SinceSequence">
/// Replay the retained event log from this sequence. Null replays the tail (head - ReplayWindow,
/// clamped to 1) so retained history is restored; an explicit value is passed through verbatim —
/// note the daemon skips the retained replay entirely when the requested sequence predates the
/// oldest retained envelope.
/// </param>
/// <param name="ReplayWindow">
/// Width of the default replay tail when <see cref="SinceSequence"/> is null. Kept for API
/// compatibility with the plan; the merged daemon surface has no <c>daemon.replayWindow</c>
/// setting, so the retained log (server-side capacity) bounds the replay. Default 5000.
/// </param>
/// <param name="AutoHandleUiRequests">
/// When true, every <c>ui_request</c> event is answered with an automatic decline so a headless
/// consumer never blocks a turn. When false and no handler is installed via
/// <see cref="SessionConnection.SetUiRequestHandler"/>, requests queue until a handler is installed
/// (or are declined on dispose). Default false.
/// </param>
public sealed record AttachOptions(
    long? SinceSequence = null,
    int ReplayWindow = 5000,
    bool AutoHandleUiRequests = false);
