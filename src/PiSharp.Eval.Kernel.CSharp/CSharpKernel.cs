using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using PiSharp.Eval.Kernels;

namespace PiSharp.Eval.Kernel.CSharp;

/// <summary>
/// The default eval kernel: in-process Roslyn C# scripting. A <see cref="ScriptState"/>
/// is kept alive across <c>eval</c> calls, so variables persist between executions for
/// free. Executions are serialized by a per-kernel gate. A runaway CPU loop cannot be
/// hard-killed in-process; on timeout the kernel is marked <b>poisoned</b> and the next
/// execution transparently resets it (<see cref="KernelExecuteResult.WasReset"/>).
/// </summary>
public sealed class CSharpKernel : IKernel, IKernelInfo
{
    public const string KernelName = "csharp";
    public const int DefaultTimeoutMs = 30000;
    public const int MaxOutputBytes = 256 * 1024;
    public const string TruncationMarker = "\n... [output truncated]";
    public const int SnapshotSchemaVersion = 1;

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex TypeNamePattern = new("^[A-Za-z0-9_<>\\[\\]., ]+$", RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScriptState? _state;
    private ScriptOptions _options;
    private KernelHost? _host;
    private string _cwd = ".";
    private IKernelToolBridge? _toolBridge;
    private string? _sessionId;
    private volatile bool _running;
    private volatile bool _capturingOutput;
    private bool _poisoned;
    private bool _lastSnapshotLossy;
    private long _generation;
    private readonly List<string> _pendingWarnings = [];

    public string Name => KernelName;
    public string Language => "csharp";
    public bool IsRunning => _running;

    public int VariableCount => _state?.Variables.Length ?? 0;
    public bool? LastSnapshotLossy => _lastSnapshotLossy;

    private static string KernelVersion =>
        typeof(CSharpKernel).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public Task StartAsync(KernelStartOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _cwd = options.Cwd;
        _toolBridge = options.ToolBridge;
        _sessionId = options.SessionId;
        _options = CreateScriptOptions();
        _host = new KernelHost(_cwd, _toolBridge, LogLine);
        _state = null;
        _poisoned = false;
        _running = true;
        Interlocked.Increment(ref _generation);
        _pendingWarnings.Clear();
        return options.RestoreSnapshot is null
            ? Task.CompletedTask
            : RestoreAsync(options.RestoreSnapshot, ct);
    }

    public async Task<KernelExecuteResult> ExecuteAsync(string code, KernelExecuteOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(!_running, this);

            var wasReset = _poisoned || options?.Reset == true;
            if (wasReset || _state is null)
            {
                _state = null;
                _poisoned = false;
                Interlocked.Increment(ref _generation);
            }

            var output = new StringBuilder();
            foreach (var warning in _pendingWarnings)
            {
                output.AppendLine(warning);
            }
            _pendingWarnings.Clear();

            var sw = Stopwatch.StartNew();
            var timeoutMs = options?.TimeoutMs ?? DefaultTimeoutMs;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs > 0) cts.CancelAfter(timeoutMs);

            var originalOut = Console.Out;
            var originalError = Console.Error;
            try
            {
                var capture = new StringWriter();
                Console.SetOut(capture);
                Console.SetError(capture);
                _capturingOutput = true;

                var generation = Interlocked.Read(ref _generation);
                var runTask = RunCoreAsync(code, generation, cts.Token);
                var completed = await Task.WhenAny(runTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
                if (completed != runTask)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    // CPU-bound scripts cannot be interrupted; poison and let the next call reset.
                    _poisoned = true;
                    Interlocked.Increment(ref _generation);
                    sw.Stop();
                    output.Append(capture.ToString());
                    return new KernelExecuteResult(
                        TruncateOutput(output.ToString()),
                        IsError: true,
                        sw.Elapsed.TotalMilliseconds,
                        TimedOut: true,
                        wasReset,
                        Snapshot: null);
                }

                await runTask;
                sw.Stop();
                output.Append(capture.ToString());
                return new KernelExecuteResult(
                    TruncateOutput(output.ToString()),
                    IsError: false,
                    sw.Elapsed.TotalMilliseconds,
                    TimedOut: false,
                    wasReset,
                    Snapshot: null);
            }
            catch (CompilationErrorException cex)
            {
                sw.Stop();
                foreach (var diagnostic in cex.Diagnostics)
                {
                    output.AppendLine(diagnostic.ToString());
                }
                return new KernelExecuteResult(
                    TruncateOutput(output.ToString()),
                    IsError: true,
                    sw.Elapsed.TotalMilliseconds,
                    TimedOut: false,
                    wasReset,
                    Snapshot: null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _poisoned = true;
                Interlocked.Increment(ref _generation);
                sw.Stop();
                return new KernelExecuteResult(
                    TruncateOutput(output.ToString()),
                    IsError: true,
                    sw.Elapsed.TotalMilliseconds,
                    TimedOut: true,
                    wasReset,
                    Snapshot: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                output.AppendLine(ex.ToString());
                return new KernelExecuteResult(
                    TruncateOutput(output.ToString()),
                    IsError: true,
                    sw.Elapsed.TotalMilliseconds,
                    TimedOut: false,
                    wasReset,
                    Snapshot: null);
            }
            finally
            {
                _capturingOutput = false;
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                cts.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<KernelSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var state = _state;
        var imports = _options.Imports.ToArray();
        if (state is null)
        {
            return Task.FromResult(new KernelSnapshot(
                SnapshotSchemaVersion, Name, KernelVersion, DateTimeOffset.UtcNow,
                Lossy: false, Variables: [], imports));
        }

        var variables = new List<KernelVariableSnapshot>(state.Variables.Length);
        var lossy = false;
        foreach (var variable in state.Variables)
        {
            string? json = null;
            var variableLossy = false;
            try
            {
                json = JsonSerializer.Serialize(variable.Value, variable.Type, WebJsonOptions);
            }
            catch (Exception)
            {
                variableLossy = true;
                lossy = true;
            }
            variables.Add(new KernelVariableSnapshot(variable.Name, ToCSharpTypeName(variable.Type), json, variableLossy));
        }

        return Task.FromResult(new KernelSnapshot(
            SnapshotSchemaVersion, Name, KernelVersion, DateTimeOffset.UtcNow,
            lossy, variables, imports));
    }

    public async Task RestoreAsync(KernelSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(!_running, this);
            _options = CreateScriptOptions(snapshot.Imports);
            _host = new KernelHost(_cwd, _toolBridge, LogLine);
            _state = null;
            _poisoned = false;
            Interlocked.Increment(ref _generation);
            _lastSnapshotLossy = snapshot.Lossy;
            _pendingWarnings.Clear();

            if (snapshot.Variables.Count == 0) return;

            var script = BuildRestoreScript(snapshot, out var lossyCount);
            var setup = await CSharpScript.RunAsync(script, _options, _host, cancellationToken: ct);
            _state = setup;
            if (lossyCount > 0)
            {
                _pendingWarnings.Add(
                    $"[eval kernel] restored {snapshot.Variables.Count} variables; {lossyCount} lossy value(s) restored as null.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        _state = null;
        _poisoned = false;
        Interlocked.Increment(ref _generation);
        _pendingWarnings.Clear();
        _host = new KernelHost(_cwd, _toolBridge, LogLine);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _running = false;
        _state = null;
        return ValueTask.CompletedTask;
    }

    private async Task RunCoreAsync(string code, long generation, CancellationToken ct)
    {
        var current = _state;
        var next = current is null
            ? await CSharpScript.RunAsync(code, _options, _host, cancellationToken: ct)
            : await current.ContinueWithAsync(code, _options, ct);

        // A timed-out (poisoned) execution may still complete in the background; only adopt
        // its state while this execution is still the current owner.
        if (Interlocked.Read(ref _generation) != generation) return;

        _state = next;
        if (next.ReturnValue is not null)
        {
            var text = next.ReturnValue.ToString();
            if (!string.IsNullOrEmpty(text)) LogLine(text);
        }
    }

    private void LogLine(string message)
    {
        if (message is null || !_capturingOutput) return;
        Console.Out.WriteLine(message);
    }

    private static ScriptOptions CreateScriptOptions(IReadOnlyList<string>? imports = null)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();

        var defaultImports = new[]
        {
            "System", "System.Collections.Generic", "System.IO", "System.Linq",
            "System.Text", "System.Threading", "System.Threading.Tasks", "System.Text.Json",
            "PiSharp.Eval.Kernel.CSharp",
        };
        var allImports = imports is { Count: > 0 }
            ? defaultImports.Concat(imports).Distinct(StringComparer.Ordinal).ToArray()
            : defaultImports;

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(allImports)
            .WithEmitDebugInformation(false);
    }

    private static string TruncateOutput(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxOutputBytes) return text;
        var end = Math.Min(text.Length, MaxOutputBytes);
        while (end > 0 && (text[end - 1] & 0xC0) == 0x80) end--;
        return text[..end] + TruncationMarker;
    }

    /// <summary>
    /// Generates a setup script re-declaring each snapshot variable via
    /// <c>KernelGlobals.FromJson&lt;T&gt;(json)</c>. Lossy variables (or variables whose
    /// type does not resolve in a fresh compilation — e.g. types declared inside eval code)
    /// restore as <c>object</c> with a warning.
    /// </summary>
    internal static string BuildRestoreScript(KernelSnapshot snapshot, out int lossyCount)
    {
        var sb = new StringBuilder();
        lossyCount = 0;
        foreach (var variable in snapshot.Variables)
        {
            if (!IdentifierPattern.IsMatch(variable.Name) ||
                !TypeNamePattern.IsMatch(variable.TypeName ?? string.Empty))
            {
                lossyCount++;
                continue;
            }

            var jsonLiteral = JsonSerializer.Serialize(variable.Json ?? string.Empty);
            if (variable.Lossy || variable.Json is null)
            {
                sb.AppendLine($"object {variable.Name} = KernelGlobals.FromJson<object>(null);");
                lossyCount++;
            }
            else
            {
                sb.AppendLine($"{variable.TypeName} {variable.Name} = KernelGlobals.FromJson<{variable.TypeName}>({jsonLiteral});");
            }
        }
        return sb.ToString();
    }

    private static string ToCSharpTypeName(Type type)
    {
        if (type.IsArray)
            return ToCSharpTypeName(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            var fullName = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = fullName.IndexOf('`');
            var baseName = tick >= 0 ? fullName[..tick] : fullName;
            var args = string.Join(", ", type.GetGenericArguments().Select(ToCSharpTypeName));
            return $"{baseName}<{args}>";
        }
        return type.FullName ?? type.Name;
    }
}
