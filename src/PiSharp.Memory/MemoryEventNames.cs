namespace PiSharp.Memory;

/// <summary>
/// Extension-emitted event names (snake_case, following <see cref="PiSharp.Extensions.ExtensionEventNames"/>
/// conventions). Delivered on the per-session event stream via
/// <c>IExtensionApi.EmitClientEventAsync</c>.
/// </summary>
public static class MemoryEventNames
{
    /// <summary>Payload: { backend, previous } — the active backend changed.</summary>
    public const string MemoryBackendChanged = "memory_backend_changed";

    /// <summary>Payload: { scope, recordKey, kind, action: "put"|"update"|"invalidate"|"delete" }.</summary>
    public const string MemoryRecordChanged = "memory_record_changed";

    /// <summary>Payload: { toolCalls, autoContinue } — an auto-learn capture turn is starting.</summary>
    public const string AutolearnCaptureStart = "autolearn_capture_start";

    /// <summary>Payload: { lessonsStored, skillsCreated, declined } — the capture turn finished.</summary>
    public const string AutolearnCaptureEnd = "autolearn_capture_end";
}
