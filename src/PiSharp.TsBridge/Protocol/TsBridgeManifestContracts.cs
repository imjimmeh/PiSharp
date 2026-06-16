namespace PiSharp.TsBridge.Protocol;

public sealed record TsBridgeManifest(
    int SchemaVersion,
    IReadOnlyList<TsBridgeModuleShim> ModuleShims,
    TsBridgeProtocolManifest Protocol,
    TsBridgeApiSurfaceManifest ApiSurface);

public sealed record TsBridgeModuleShim(
    string Specifier,
    string CacheFileName,
    string Description,
    IReadOnlyList<TsBridgeShimExport> Exports);

public sealed record TsBridgeShimExport(
    string Name,
    string Kind,
    string? Helper = null,
    string? Message = null,
    object? Value = null,
    IReadOnlyList<TsBridgeShimExport>? Members = null,
    string? RuntimeAction = null);

public sealed record TsBridgeProtocolManifest(
    IReadOnlyDictionary<string, string> Methods,
    IReadOnlyDictionary<string, string> RuntimeActions);

public sealed record TsBridgeApiSurfaceManifest(
    IReadOnlyList<TsBridgeApiMember> Members,
    IReadOnlyList<TsBridgeRuntimeSnapshotField> RuntimeSnapshotFields,
    IReadOnlyList<TsBridgeEventContract> Events);

public sealed record TsBridgeApiMember(
    string Surface,
    string Name,
    string Kind,
    string Status,
    string? RuntimeAction = null,
    string? SnapshotField = null,
    string? UnsupportedReason = null,
    string? OwnerPhase = null);

public sealed record TsBridgeRuntimeSnapshotField(
    string Name,
    string JsonShape,
    bool RequiredForActivation,
    bool Refreshable);

public sealed record TsBridgeEventContract(
    string Name,
    string PayloadShape,
    string ContextShape,
    bool Cancellable,
    bool MutablePayload,
    bool ChainsReturnValue,
    string Status,
    string? OwnerPhase = null);

public static class TsBridgeManifestSchema
{
    public const int CurrentVersion = 1;
}

public static class TsBridgeShimExportKinds
{
    public const string Helper = "helper";
    public const string JsonConst = "json-const";
    public const string UnavailableFunction = "unavailable-function";
    public const string AsyncUnavailableFunction = "async-unavailable-function";
    public const string RuntimeFunction = "runtime-function";
    public const string Namespace = "namespace";
}

public static class TsBridgeApiMemberStatuses
{
    public const string Implemented = "implemented";
    public const string Snapshot = "snapshot";
    public const string RuntimeAction = "runtime-action";
    public const string StubUnavailable = "stub-unavailable";
}
