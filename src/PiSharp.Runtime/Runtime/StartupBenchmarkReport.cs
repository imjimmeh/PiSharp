using PiSharp.TsBridge.Protocol;

namespace PiSharp.Runtime;

public sealed record StartupBenchmarkReport(
    TimeSpan Total,
    IReadOnlyList<StartupBenchmarkPhase> Phases,
    IReadOnlyList<StartupBenchmarkExtensionTiming> NativeExtensions,
    IReadOnlyList<StartupBenchmarkExtensionTiming> TypeScriptExtensions);

public sealed record StartupBenchmarkPhase(string Name, TimeSpan Duration);

public sealed record StartupBenchmarkExtensionTiming(
    string Path,
    TimeSpan LoadDuration,
    TimeSpan InitializeDuration,
    TimeSpan Total,
    bool Success,
    string? Error = null,
    TsExtensionLoadTimings? BridgeTimings = null);
