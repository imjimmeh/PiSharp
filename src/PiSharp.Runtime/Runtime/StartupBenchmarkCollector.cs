using System.Diagnostics;

namespace PiSharp.Runtime;

internal sealed class StartupBenchmarkCollector
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<StartupBenchmarkPhase> _phases = [];
    private readonly List<StartupBenchmarkExtensionTiming> _native = [];
    private readonly List<StartupBenchmarkExtensionTiming> _typescript = [];

    public void AddPhase(string name, TimeSpan duration) => _phases.Add(new StartupBenchmarkPhase(name, duration));

    public void AddNativeExtension(string path, TimeSpan loadDuration, TimeSpan initializeDuration, bool success, string? error = null)
        => _native.Add(new StartupBenchmarkExtensionTiming(path, loadDuration, initializeDuration, loadDuration + initializeDuration, success, error));

    public void AddTypeScriptExtension(string path, TimeSpan loadDuration, TimeSpan initializeDuration, bool success, string? error = null, PiSharp.TsBridge.Protocol.TsExtensionLoadTimings? bridgeTimings = null)
        => _typescript.Add(new StartupBenchmarkExtensionTiming(path, loadDuration, initializeDuration, loadDuration + initializeDuration, success, error, bridgeTimings));

    public StartupBenchmarkReport Build()
        => new(_total.Elapsed, _phases.ToArray(), _native.ToArray(), _typescript.ToArray());
}
