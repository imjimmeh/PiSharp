namespace PiSharp.Sdk;

/// <summary>
/// Static metadata about the SDK's wire protocol. The daemon protocol is versioned so that a client
/// and daemon must speak the same protocol before commands are exchanged.
/// </summary>
public static class PiSharpSdk
{
    /// <summary>
    /// The protocol version this SDK speaks (P22 plan §3.3: NuGet major == protocol version). The
    /// daemon does not currently expose a protocol constant on the wire; the SDK enforces
    /// compatibility by refusing leases whose runtime version does not match the local runtime
    /// (mirroring <c>DaemonDiscovery.IsRuntimeCompatible</c>).
    /// </summary>
    public const string ProtocolVersion = "1";
}
