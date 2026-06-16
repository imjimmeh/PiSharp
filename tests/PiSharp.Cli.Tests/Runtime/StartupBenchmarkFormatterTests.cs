using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.Cli.Tests.Runtime;

public sealed class StartupBenchmarkFormatterTests
{
    [Fact]
    public void RenderIncludesTypeScriptBridgeSubPhaseTimings()
    {
        var report = new StartupBenchmarkReport(
            TimeSpan.FromMilliseconds(100),
            [new StartupBenchmarkPhase("extensions.ts.bridge.start", TimeSpan.FromMilliseconds(5))],
            [],
            [new StartupBenchmarkExtensionTiming(
                "extension.ts",
                TimeSpan.FromMilliseconds(20),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20),
                true,
                BridgeTimings: new TsExtensionLoadTimings(
                    CacheLookup: 1,
                    CompilerLoad: 2,
                    Transpile: 3,
                    DependencyTranspile: 4,
                    ModuleImport: 5,
                    Activation: 6,
                    RegistrationFlush: 7,
                    Total: 8,
                    CacheHits: 9,
                    CacheMisses: 10,
                    CacheFallbacks: 11))]);

        var rendered = StartupBenchmarkFormatter.Render(report);

        Assert.Contains("extensions.ts.bridge.start", rendered);
        Assert.Contains("bridge: cache: 1.00 ms", rendered);
        Assert.Contains("compiler: 2.00 ms", rendered);
        Assert.Contains("transpile: 3.00 ms", rendered);
        Assert.Contains("registrations: 7.00 ms", rendered);
        Assert.Contains("cache hits: 9  misses: 10  fallbacks: 11", rendered);
    }
}
