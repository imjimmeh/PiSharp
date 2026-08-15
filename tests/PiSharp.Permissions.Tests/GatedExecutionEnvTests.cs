using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Extensions;
using PiSharp.Permissions.Tests.Fakes;
using Xunit;

namespace PiSharp.Permissions.Tests;

[CollectionDefinition("CapabilityGates", DisableParallelization = true)]
public sealed class CapabilityGatesCollection
{
}

/// <summary>
/// Exercises the <see cref="GatedExecutionEnv"/> wrapper around extension shell execution.
/// The static <see cref="CapabilityGates.ShellExec"/> seam is mutated here, so this class runs
/// non-parallel with itself and other gate tests.
/// </summary>
[Collection("CapabilityGates")]
public sealed class GatedExecutionEnvTests
{
    private sealed class ScriptableEnv : FakeExecutionEnv
    {
        public List<string> ExecutedCommands { get; } = [];

        public override Task<Result<ShellResult, ExecutionError>> ExecAsync(
            string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        {
            ExecutedCommands.Add(command);
            return Task.FromResult(Result<ShellResult, ExecutionError>.Ok(new ShellResult("out", "", 0)));
        }
    }

    private static string BlockAll(SpawnRequest request) => $"blocked: {request.Command}";

    [Fact]
    public async Task ExecAsync_DeniedByExplicitGate_ReturnsSpawnErrorWithoutInvokingInner()
    {
        var inner = new ScriptableEnv();
        var gated = new GatedExecutionEnv(inner, explicitGate: BlockAll);

        var result = await gated.ExecAsync("rm -rf /", cancellationToken: CancellationToken.None);

        Assert.True(result.IsErr);
        Assert.Equal(ExecutionErrorCode.SpawnError, result.Error.Code);
        Assert.Contains("permission gate", result.Error.Message);
        Assert.Empty(inner.ExecutedCommands);
    }

    [Fact]
    public async Task ExecAsync_AllowedByExplicitGate_ForwardsToInner()
    {
        var inner = new ScriptableEnv();
        var gated = new GatedExecutionEnv(inner, explicitGate: _ => null);

        var result = await gated.ExecAsync("git status", cancellationToken: CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(["git status"], inner.ExecutedCommands);
    }

    [Fact]
    public async Task ExecAsync_NoGateAndNoStaticSeam_ForwardsToInner()
    {
        var previous = CapabilityGates.ShellExec;
        try
        {
            CapabilityGates.ShellExec = null;
            var inner = new ScriptableEnv();
            var gated = new GatedExecutionEnv(inner);

            var result = await gated.ExecAsync("whoami", cancellationToken: CancellationToken.None);

            Assert.True(result.IsOk);
            Assert.Equal(["whoami"], inner.ExecutedCommands);
        }
        finally
        {
            CapabilityGates.ShellExec = previous;
        }
    }

    [Fact]
    public async Task ExecAsync_StaticSeamUsedWhenNoExplicitGate()
    {
        var previous = CapabilityGates.ShellExec;
        try
        {
            CapabilityGates.ShellExec = BlockAll;
            var inner = new ScriptableEnv();
            var gated = new GatedExecutionEnv(inner);

            var result = await gated.ExecAsync("ls", cancellationToken: CancellationToken.None);

            Assert.True(result.IsErr);
            Assert.Equal(ExecutionErrorCode.SpawnError, result.Error.Code);
            Assert.Empty(inner.ExecutedCommands);
        }
        finally
        {
            CapabilityGates.ShellExec = previous;
        }
    }

    [Fact]
    public async Task FileSystemMembers_ForwardToInner()
    {
        var inner = new FakeExecutionEnv { Cwd = "C:/work" };
        inner.AddExistingFile("C:/work/a.txt");
        var gated = new GatedExecutionEnv(inner, explicitGate: BlockAll);

        Assert.Equal("C:/work", gated.Cwd);
        var exists = await gated.ExistsAsync("C:/work/a.txt", CancellationToken.None);
        Assert.True(exists.IsOk && exists.Value);
    }

    [Fact]
    public async Task ExecAsync_ReasonText_NamesTheBlockedCommand()
    {
        var inner = new ScriptableEnv();
        var gated = new GatedExecutionEnv(inner, BlockAll);

        var result = await gated.ExecAsync("proc", cancellationToken: CancellationToken.None);

        Assert.True(result.IsErr);
        Assert.Contains("blocked: proc", result.Error.Message);
    }
}
