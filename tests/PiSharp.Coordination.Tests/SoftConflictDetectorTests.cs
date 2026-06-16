using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class SoftConflictDetectorTests
{
    [Fact]
    public void WarnsWhenAnotherAgentEditedFileAfterCurrentAgentReadIt()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.True(decision.ShouldWarn);
        Assert.Contains("agent-b", decision.Message);
        Assert.Contains("re-read", decision.Message);
    }

    [Fact]
    public void NoWarningWhenNoConflictingWrite()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.False(decision.ShouldWarn);
        Assert.Empty(decision.Message);
    }

    [Fact]
    public void NoWarningWhenAgentReReadAfterConflictingWrite()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(3)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(4));

        Assert.False(decision.ShouldWarn);
    }

    [Fact]
    public void NoWarningWhenAgentWritesAfterOwnReadWithoutOtherAgent()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.False(decision.ShouldWarn);
    }

    [Fact]
    public void NoWarningWhenAgentHasNotReadFile()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.False(decision.ShouldWarn);
    }

    [Fact]
    public void NoWarningForUnrelatedFile()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "notes.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "notes.md", DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.False(decision.ShouldWarn);
    }

    [Fact]
    public void NoWarningWhenWritePrecedesRead()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.False(decision.ShouldWarn);
    }

    [Fact]
    public void NoWarningOnRepeatedAttemptAfterWarningWithNoNewerConflict()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var firstDecision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(3));
        Assert.True(firstDecision.ShouldWarn);

        state.Apply(new PreflightWarningRecord("agent-a", "README.md", "agent-b", DateTimeOffset.UnixEpoch.AddSeconds(2), DateTimeOffset.UnixEpoch.AddSeconds(3)));

        var secondDecision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(4));

        Assert.False(secondDecision.ShouldWarn);
    }

    [Fact]
    public void WarnsAgainWhenNewerConflictAppearsAfterWarning()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-c", 3, "session-c", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        state.Apply(new PreflightWarningRecord("agent-a", "README.md", "agent-b", DateTimeOffset.UnixEpoch.AddSeconds(2), DateTimeOffset.UnixEpoch.AddSeconds(3)));

        state.Apply(new FileWriteRecord("agent-c", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(4)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(5));

        Assert.True(decision.ShouldWarn);
        Assert.Contains("agent-c", decision.Message);
    }

    [Fact]
    public void NoWarningWhenRepeatedAttemptWithSameConflictAlreadyWarnedFor()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        state.Apply(new PreflightWarningRecord("agent-a", "README.md", "agent-b", DateTimeOffset.UnixEpoch.AddSeconds(2), DateTimeOffset.UnixEpoch.AddSeconds(3)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "README.md", DateTimeOffset.UnixEpoch.AddSeconds(4));

        Assert.False(decision.ShouldWarn);
    }

    [Fact]
    public void WarnsWhenPriorReadIsRelativeAndPreflightUsesNormalizedPath()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));

        state.Apply(new FileReadRecord("agent-a", "src/project/main.cs", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "src/project/main.cs", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "src/project/main.cs", DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.True(decision.ShouldWarn);
        Assert.Contains("agent-b", decision.Message);
    }

    [Fact]
    public void PreflightHandlesNormalizedPathMatchingRepoRelativeStoredPaths()
    {
        var state = new CoordinationState();
        state.Apply(new AgentRegisteredRecord("agent-a", 1, "session-a", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentRegisteredRecord("agent-b", 2, "session-b", null, "/repo", DateTimeOffset.UnixEpoch));

        state.Apply(new FileReadRecord("agent-a", "src/app/main.cs", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        state.Apply(new FileWriteRecord("agent-b", "src/app/main.cs", DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var decision = SoftConflictDetector.PreflightWrite(state, "agent-a", "src/app/main.cs", DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.True(decision.ShouldWarn);
    }
}
