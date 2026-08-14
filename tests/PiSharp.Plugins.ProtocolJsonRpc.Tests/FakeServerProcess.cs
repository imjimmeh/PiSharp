using System.Diagnostics;
using System.IO.Pipelines;
using PiSharp.Plugins.ProtocolJsonRpc.Process;

namespace PiSharp.Plugins.ProtocolJsonRpc.Tests;

/// <summary>
/// In-memory <see cref="IServerProcess"/>: the client's <see cref="StandardInput"/>/
/// <see cref="StandardOutput"/> are pipe-backed streams; the fake server side reads client
/// writes from <see cref="ServerInput"/> and writes responses/events to
/// <see cref="ServerOutput"/>. No OS processes are involved.
/// </summary>
public sealed class FakeServerProcess(ProcessStartInfo startInfo) : IServerProcess
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private bool _completed;

    public ProcessStartInfo StartInfo { get; } = startInfo;

    public Stream StandardInput => _clientToServer.Writer.AsStream();

    public Stream StandardOutput => _serverToClient.Reader.AsStream();

    public Stream ServerInput => _clientToServer.Reader.AsStream();

    public Stream ServerOutput => _serverToClient.Writer.AsStream();

    public bool HasExited { get; private set; }

    /// <summary>Records the most recent <c>Kill(entireProcessTree)</c> invocation.</summary>
    public bool LastKillEntireProcessTree { get; private set; }

    public bool KillCalled { get; private set; }

    public event Action<string?>? StandardErrorReceived;

    public void BeginErrorReadLine()
    {
    }

    /// <summary>Fakes the server emitting a stderr line.</summary>
    public void EmitStderr(string line) => StandardErrorReceived?.Invoke(line);

    /// <summary>Simulates process death: marks the process exited and completes the pipes (EOF for the client pump).</summary>
    public void SimulateExit()
    {
        HasExited = true;
        CompletePipes();
    }

    public void Kill(bool entireProcessTree)
    {
        KillCalled = true;
        LastKillEntireProcessTree = entireProcessTree;
        HasExited = true;
        CompletePipes();
    }

    public void Dispose() => CompletePipes();

    private void CompletePipes()
    {
        if (_completed) return;
        _completed = true;
        try { _clientToServer.Writer.Complete(); } catch (InvalidOperationException) { }
        try { _serverToClient.Writer.Complete(); } catch (InvalidOperationException) { }
    }
}

/// <summary>
/// Records spawned fake processes so tests can drive the server side and inspect the
/// <see cref="ProcessStartInfo"/> the production code built. Each <see cref="NextProcessAsync"/>
/// await returns the next spawned process (a FIFO of pending spawns).
/// </summary>
public sealed class FakeServerProcessFactory : IServerProcessFactory
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<FakeServerProcess>> _waiters = new();
    private readonly Queue<FakeServerProcess> _pending = new();

    public List<FakeServerProcess> Processes { get; } = [];

    public Task<FakeServerProcess> NextProcessAsync
    {
        get
        {
            lock (_gate)
            {
                if (_pending.Count > 0) return Task.FromResult(_pending.Dequeue());
                var waiter = new TaskCompletionSource<FakeServerProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
                return waiter.Task;
            }
        }
    }

    public IServerProcess Start(ProcessStartInfo startInfo)
    {
        var process = new FakeServerProcess(startInfo);
        Processes.Add(process);
        lock (_gate)
        {
            if (_waiters.Count > 0) _waiters.Dequeue().TrySetResult(process);
            else _pending.Enqueue(process);
        }
        return process;
    }
}
