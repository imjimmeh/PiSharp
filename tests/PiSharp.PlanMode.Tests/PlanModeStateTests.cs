using PiSharp.PlanMode;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanModeStateTests
{
    [Fact]
    public void State_SameCollectionInstances_AreValueEqual()
    {
        // Record equality compares the IReadOnlyList member by reference (arrays do not
        // override Equals); two states built from the SAME collection instance are equal.
        var tools = new[] { "read", "grep" };
        var a = new PlanModeState(PlanModePhase.Planning, tools, "planning-model", "C:/project/.pi/plans/plan-session-1.md");
        var b = new PlanModeState(PlanModePhase.Planning, tools, "planning-model", "C:/project/.pi/plans/plan-session-1.md");

        Assert.Equal(a, b);
    }

    [Fact]
    public void State_WithExpression_ProducesNewImmutableSnapshot()
    {
        var state = new PlanModeState(PlanModePhase.Planning, ["read", "grep"], "planning-model", "plan-session-1.md");

        var next = state with { Phase = PlanModePhase.Executing, RestrictedToolNames = [] };

        Assert.Equal(PlanModePhase.Executing, next.Phase);
        Assert.Empty(next.RestrictedToolNames);
        Assert.Equal(PlanModePhase.Planning, state.Phase); // original untouched
        Assert.Equal(["read", "grep"], state.RestrictedToolNames);
    }

    [Fact]
    public void DefaultState_IsInactiveWithEmptyRestrictedTools()
    {
        var state = new PlanModeState(PlanModePhase.Inactive, [], null, null);

        Assert.Equal(PlanModePhase.Inactive, state.Phase);
        Assert.Empty(state.RestrictedToolNames);
        Assert.Null(state.PlanningModel);
        Assert.Null(state.PlanFile);
    }

    [Fact]
    public void PhaseEnum_HasAllFourContractValues()
    {
        var values = Enum.GetValues<PlanModePhase>();
        Assert.Equal(4, values.Length);
        Assert.Contains(PlanModePhase.Inactive, values);
        Assert.Contains(PlanModePhase.Planning, values);
        Assert.Contains(PlanModePhase.Executing, values);
        Assert.Contains(PlanModePhase.Aborted, values);
    }
}
