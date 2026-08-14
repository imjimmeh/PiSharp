using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.Eval;

/// <summary>
/// Resolved eval settings. Keys live under the P02 <c>extensions</c> container
/// (<c>extensions.pisharp-eval.&lt;key&gt;</c>, read via the namespaced
/// <c>IExtensionSettingsApi</c>); the pre-P02 direct paths (<c>eval.kernel.*</c> etc.) are
/// honored as a fallback via <see cref="IExtensionSettingsApi.GetCore"/>.
/// </summary>
public sealed record EvalOptions
{
    public const string DefaultKernelName = "csharp";
    public const int DefaultTimeoutMs = 30000;
    public const int DefaultSnapshotMaxBytes = 1048576;

    public string DefaultKernel { get; init; } = DefaultKernelName;
    public int KernelTimeoutMs { get; init; } = DefaultTimeoutMs;
    public IReadOnlyList<string> LoopbackTools { get; init; } = ["read", "grep", "find", "ls"];
    public bool RestoreOnStart { get; init; } = true;
    public int SnapshotMaxBytes { get; init; } = DefaultSnapshotMaxBytes;
    public int SavePointIntervalMs { get; init; }
    public string BenchDir { get; init; } = ".pi/bench";
    public int BenchDefaultRuns { get; init; } = 1;
    public int BenchParallelism { get; init; } = 1;
    public string? BenchProvider { get; init; }
    public string? BenchModel { get; init; }

    public static EvalOptions Read(IExtensionApi api, string cwd)
    {
        var settings = api.Settings;
        return new EvalOptions
        {
            DefaultKernel = GetString(settings, "kernel.default", "eval.kernel.default") ?? DefaultKernelName,
            KernelTimeoutMs = GetInt(settings, "kernel.timeoutMs", "eval.kernel.timeoutMs") ?? DefaultTimeoutMs,
            LoopbackTools = GetStringList(settings, "kernel.loopbackTools", "eval.kernel.loopbackTools") ?? ["read", "grep", "find", "ls"],
            RestoreOnStart = GetBool(settings, "kernel.restoreOnStart", "eval.kernel.restoreOnStart") ?? true,
            SnapshotMaxBytes = GetInt(settings, "kernel.snapshotMaxBytes", "eval.kernel.snapshotMaxBytes") ?? DefaultSnapshotMaxBytes,
            SavePointIntervalMs = GetInt(settings, "kernel.savePointIntervalMs", "eval.kernel.savePointIntervalMs") ?? 0,
            BenchDir = GetString(settings, "benchDir", "eval.benchDir") ?? Path.Combine(cwd, ".pi", "bench"),
            BenchDefaultRuns = GetInt(settings, "bench.defaultRuns", "eval.bench.defaultRuns") ?? 1,
            BenchParallelism = GetInt(settings, "bench.parallelism", "eval.bench.parallelism") ?? 1,
            BenchProvider = GetString(settings, "bench.provider", "eval.bench.provider"),
            BenchModel = GetString(settings, "bench.model", "eval.bench.model"),
        };
    }

    private static string? GetString(IExtensionSettingsApi settings, string key, string coreKey)
    {
        try
        {
            var v = settings.Get(key) ?? settings.GetCore(coreKey);
            return v switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? GetInt(IExtensionSettingsApi settings, string key, string coreKey)
    {
        try
        {
            var v = settings.Get(key) ?? settings.GetCore(coreKey);
            return v switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var n) => n,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? GetBool(IExtensionSettingsApi settings, string key, string coreKey)
    {
        try
        {
            var v = settings.Get(key) ?? settings.GetCore(coreKey);
            return v switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } je => je.GetBoolean(),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? GetStringList(IExtensionSettingsApi settings, string key, string coreKey)
    {
        try
        {
            var v = settings.Get(key) ?? settings.GetCore(coreKey);
            return v switch
            {
                IReadOnlyList<string> list => list,
                JsonElement { ValueKind: JsonValueKind.Array } je => je.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToArray(),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
