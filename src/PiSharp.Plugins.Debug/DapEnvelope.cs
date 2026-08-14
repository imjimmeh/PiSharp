using System.Text.Json;

namespace PiSharp.Plugins.Debug;

/// <summary>
/// DAP wire envelopes use <c>seq</c>/<c>type</c>/<c>command</c> (base protocol §3.1), NOT
/// JSON-RPC <c>id</c>/<c>method</c>. These records model the three message kinds the
/// adapter speaks; <see cref="DapConnection"/> translates between them and the byte stream.
/// </summary>
internal sealed record DapRequestFrame(int Seq, string Command, JsonElement? Arguments);

internal sealed record DapResponseFrame(int RequestSeq, bool Success, string? Command, string? Message, JsonElement? Body);

/// <summary>An adapter-initiated <c>type: "event"</c> message (stopped, continued, output, ...).</summary>
public sealed record DapEvent(string Name, JsonElement? Body);
