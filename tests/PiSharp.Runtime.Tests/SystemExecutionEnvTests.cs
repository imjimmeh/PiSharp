using System.Diagnostics;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Runtime.Tests;

/// <summary>
/// Guards <see cref="SystemExecutionEnv.ExecAsync"/> against orphaned child
/// process trees on cancellation/timeout, unbounded output capture, and
/// sync-over-async stalls in the stdout/stderr readers.
/// </summary>
public sealed class SystemExecutionEnvTests
{
    private const long MaxCaptureBytes = 1024 * 1024;
    private const string TruncatedMarker = ".[truncated]";

    [Fact]
    public async Task ExecAsyncCancellationKillsChildProcessTree()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pidFile = Path.Combine(Path.GetTempPath(), $"pi-exec-orphan-{Guid.NewGuid():N}.pid");
        try
        {
            var env = new SystemExecutionEnv(Path.GetTempPath());
            using var cts = new CancellationTokenSource();
            var execTask = env.ExecAsync(LongLivedChildCommand(pidFile), cancellationToken: cts.Token);

            var pid = await PollAsync(() => TryReadPidFile(pidFile), TimeSpan.FromSeconds(5), watchdog.Token);
            Assert.True(pid > 0, "child process never reported its pid");

            cts.Cancel();
            var result = await WaitForAsync(execTask, watchdog.Token);

            Assert.True(result.IsErr, "expected an aborted result");
            Assert.Equal(ExecutionErrorCode.Aborted, result.Error.Code);
            Assert.False(IsProcessAlive(pid), $"child pid {pid} still alive after cancellation");
            await WaitUntilAsync(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(5), watchdog.Token);
            Assert.False(IsProcessAlive(pid), $"child pid {pid} still alive after grace period");
            if (OperatingSystem.IsWindows())
            {
                Assert.False(TaskListShowsProcess(pid), $"tasklist still reports pid {pid}");
            }
        }
        finally
        {
            TryDeleteFile(pidFile);
        }
    }

    [Fact]
    public async Task ExecAsyncTimeoutKillsChildProcessTree()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pidFile = Path.Combine(Path.GetTempPath(), $"pi-exec-orphan-{Guid.NewGuid():N}.pid");
        try
        {
            var env = new SystemExecutionEnv(Path.GetTempPath());
            var execTask = env.ExecAsync(LongLivedChildCommand(pidFile), new ExecutionOptions(Timeout: TimeSpan.FromSeconds(4)));

            var pid = await PollAsync(() => TryReadPidFile(pidFile), TimeSpan.FromSeconds(5), watchdog.Token);
            Assert.True(pid > 0, "child process never reported its pid");

            var result = await WaitForAsync(execTask, watchdog.Token);

            Assert.True(result.IsErr, "expected a timeout result");
            Assert.Equal(ExecutionErrorCode.Timeout, result.Error.Code);
            Assert.False(IsProcessAlive(pid), $"child pid {pid} still alive after timeout");
            await WaitUntilAsync(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(5), watchdog.Token);
            Assert.False(IsProcessAlive(pid), $"child pid {pid} still alive after grace period");
        }
        finally
        {
            TryDeleteFile(pidFile);
        }
    }

    [Fact]
    public async Task ExecAsyncChattyChildWithSlowCallbackCompletesWithinBoundAndCapsCapture()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var env = new SystemExecutionEnv(Path.GetTempPath());

        async ValueTask SlowCallback(ReadOnlyMemory<byte> data, CancellationToken token)
            => await Task.Delay(5, token);

        var result = await env.ExecAsync(ChattyChildCommand(), new ExecutionOptions(OnOutputBytes: SlowCallback), cts.Token);

        Assert.True(result.IsOk, result.IsErr ? result.Error.Message : string.Empty);
        Assert.InRange(result.Value.Stdout.Length, 0, MaxCaptureBytes + 8192);
        Assert.Contains(TruncatedMarker, result.Value.Stdout);
    }

    [Fact]
    public async Task ExecAsyncCapturesStdoutWithoutCrLeakage()
    {
        var env = new SystemExecutionEnv(Path.GetTempPath());
        var command = OperatingSystem.IsWindows()
            ? "powershell -NoProfile -Command \"Write-Output 'hello'; Write-Output 'world'\""
            : "printf 'hello\\nworld\\n'";

        var result = await env.ExecAsync(command);

        Assert.True(result.IsOk, result.IsErr ? result.Error.Message : string.Empty);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal($"hello{Environment.NewLine}world{Environment.NewLine}", result.Value.Stdout);
    }

    private static string LongLivedChildCommand(string pidFile) => OperatingSystem.IsWindows()
        ? $"powershell -NoProfile -Command \"$PID | Set-Content -Path '{pidFile}'; Start-Sleep 60\""
        : $"echo \\$\\$ > {pidFile}; sleep 60";

    private static string ChattyChildCommand() => OperatingSystem.IsWindows()
        ? "powershell -NoProfile -Command \"1..300 | ForEach-Object { Write-Host ('X' * 4096) }\""
        : "awk 'BEGIN { for (i = 0; i < 300; i++) printf \"%4096s\\n\", \"\" }'";

    private static async Task<int> PollAsync(Func<int> read, TimeSpan timeout, CancellationToken watchdog)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = read();
            if (value > 0) return value;
            await Task.Delay(100, watchdog);
        }
        return 0;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken watchdog)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100, watchdog);
        }
    }

    private static async Task<T> WaitForAsync<T>(Task<T> task, CancellationToken watchdog)
    {
        var delay = Task.Delay(Timeout.InfiniteTimeSpan, watchdog);
        if (await Task.WhenAny(task, delay) != task)
        {
            throw new TimeoutException("Test watchdog expired before ExecAsync completed.");
        }
        return await task;
    }

    private static int TryReadPidFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TaskListShowsProcess(int pid)
    {
        using var process = Process.Start(new ProcessStartInfo("tasklist", $"/FI \"PID eq {pid}\" /NH")
        {
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (process is null) return true;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Contains(pid.ToString(), StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
