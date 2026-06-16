using System.Diagnostics;

namespace PiSharp.Runtime;

internal interface IRuntimeStartupPhase
{
    string Name { get; }
    Task ExecuteAsync(RuntimeStartupContext context, CancellationToken cancellationToken);
}

internal sealed class RuntimeStartupContext(PiRuntimeOptions options, StartupBenchmarkCollector? benchmark)
{
    public PiRuntimeOptions Options { get; } = options;
    public StartupBenchmarkCollector? Benchmark { get; } = benchmark;
    public List<RuntimeDiagnostic> Diagnostics { get; } = [];

    public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> action)
    {
        if (Benchmark is null) return await action();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            Benchmark.AddPhase(name, stopwatch.Elapsed);
        }
    }

    public T Measure<T>(string name, Func<T> action)
    {
        if (Benchmark is null) return action();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            Benchmark.AddPhase(name, stopwatch.Elapsed);
        }
    }
}
