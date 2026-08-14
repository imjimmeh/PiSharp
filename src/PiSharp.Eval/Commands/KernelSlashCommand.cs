using System.Text;
using PiSharp.Eval.Events;
using PiSharp.Eval.Kernels;
using PiSharp.Extensions;

namespace PiSharp.Eval.Commands;

/// <summary>
/// The <c>/kernel</c> slash command. Grammar:
/// <c>/kernel</c> (status list) | <c>/kernel reset [name]</c> |
/// <c>/kernel snapshot [name]</c> | <c>/kernel restore [name]</c>.
/// </summary>
public sealed class KernelSlashCommand
{
    private readonly KernelRegistry _registry;
    private readonly Func<string, object?, CancellationToken, Task> _emit;
    private readonly string _sessionId;
    private readonly KernelSnapshotStore _store;

    public KernelSlashCommand(
        KernelRegistry registry,
        KernelSnapshotStore store,
        Func<string, object?, CancellationToken, Task> emit,
        string sessionId)
    {
        _registry = registry;
        _store = store;
        _emit = emit;
        _sessionId = sessionId;
    }

    public async Task<string> HandleAsync(string args, CancellationToken cancellationToken = default)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = tokens.Length > 0 ? tokens[0] : "status";
        var name = tokens.Length > 1 ? tokens[1] : null;

        return verb switch
        {
            "status" or "" => Status(),
            "reset" => await ResetAsync(name, cancellationToken),
            "snapshot" => await SnapshotAsync(name, cancellationToken),
            "restore" => await RestoreAsync(name, cancellationToken),
            _ => $"Unknown /kernel subcommand '{verb}'. Usage: /kernel [reset|snapshot|restore] [kernelName]",
        };
    }

    private string Status()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Eval kernels:");
        var kernels = _registry.Kernels;
        if (kernels.Count == 0)
        {
            sb.AppendLine("  (none running — kernels start lazily on the first eval call)");
        }
        foreach (var kernel in kernels)
        {
            var snapshot = _store.LastSaved(_sessionId, kernel.Name);
            var varCount = 0;
            var lossy = snapshot?.Lossy;
            if (kernel is IKernelInfo info)
            {
                varCount = info.VariableCount;
                lossy ??= info.LastSnapshotLossy;
            }
            var snapshotAge = snapshot is null ? "none" : $"{DateTimeOffset.UtcNow - snapshot.CreatedAt:g} ago";
            sb.AppendLine($"  {kernel.Name} ({kernel.Language}, running: {kernel.IsRunning}, variables: {varCount}, snapshot: {(lossy == true ? "lossy " : "")}{snapshotAge})");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ResetAsync(string? name, CancellationToken ct)
    {
        var kernelName = ResolveKernel(name);
        await _registry.ResetAsync(kernelName, ct);
        await Emit(EvalEventNames.KernelReset,
            new EvalKernelResetEvent(kernelName, _sessionId, "explicit"), ct);
        return $"Kernel '{kernelName}' reset.";
    }

    private async Task<string> SnapshotAsync(string? name, CancellationToken ct)
    {
        var kernelName = ResolveKernel(name);
        var snapshot = await _registry.SnapshotAsync(kernelName, ct);
        await _store.SaveAsync(_sessionId, kernelName, snapshot, ct);
        await Emit(EvalEventNames.Snapshot,
            new EvalSnapshotEvent(kernelName, _sessionId, snapshot.Lossy, snapshot.Variables.Count,
                Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(snapshot))), ct);
        return $"Kernel '{kernelName}' snapshot saved ({(snapshot.Lossy ? "lossy, " : "")}{snapshot.Variables.Count} variables).";
    }

    private async Task<string> RestoreAsync(string? name, CancellationToken ct)
    {
        var kernelName = ResolveKernel(name);
        var snapshot = await _store.LoadAsync(_sessionId, kernelName, ct);
        if (snapshot is null) return $"No snapshot found for kernel '{kernelName}' in session '{_sessionId}'.";
        var kernel = await _registry.GetOrStartAsync(kernelName, ct);
        await kernel.RestoreAsync(snapshot, ct);
        await Emit(EvalEventNames.Restore,
            new EvalRestoreEvent(kernelName, _sessionId, snapshot.Lossy, snapshot.Variables.Count), ct);
        return $"Kernel '{kernelName}' restored ({(snapshot.Lossy ? "lossy, " : "")}{snapshot.Variables.Count} variables).";
    }

    private string ResolveKernel(string? name)
        => string.IsNullOrWhiteSpace(name) ? "csharp" : name!;

    private async Task Emit(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _emit(eventName, payload, ct);
        }
        catch (Exception)
        {
            // Event emission must never break the command.
        }
    }
}
