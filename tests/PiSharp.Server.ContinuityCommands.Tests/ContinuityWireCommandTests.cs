using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Continuity.Contracts;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.ContinuityCommands.Tests;

/// <summary>
/// Covers the P23 daemon wire surface (plan C5 §4.9):
/// <c>set_goal</c>, <c>get_goal</c>, <c>schedule_job</c>, <c>list_jobs</c>,
/// <c>cancel_job</c>, <c>autonomous</c>, <c>get_continuity_state</c> — against
/// an in-memory fake <see cref="IContinuitySessionService"/>, so the wire
/// behavior is covered without coupling to the real continuity engine (which
/// the plugin's own tests exercise).
/// </summary>
public sealed class ContinuityWireCommandTests
{
    // --- set_goal ---

    [Fact]
    public async Task SetGoal_InvokesServiceAndReturnsResult()
    {
        var (setObj, setTokens) = (string.Empty, (long?)null);
        var goal = NewGoal("Build the thing");
        var surface = new FakeContinuitySurface
        {
            OnSetGoal = (obj, tokens) => { setObj = obj; setTokens = tokens; return new ContinuityGoalResult(goal); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "sg", type = ServerCommandTypes.SetGoal,
            serverSessionId = ctx.SessionId,
            objective = "Build the thing",
            maxTokens = 5000
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityGoalResult>(response.Data);
        Assert.Equal("Build the thing", result.Goal!.Objective);
        Assert.Equal("Build the thing", setObj);
        Assert.Equal(5000, setTokens);
        Assert.Equal(1, surface.SetGoalCalls);
    }

    [Fact]
    public async Task SetGoal_NullMaxTokens_PassesNull()
    {
        long? captured = long.MinValue;
        var surface = new FakeContinuitySurface
        {
            OnSetGoal = (_, tokens) => { captured = tokens; return new ContinuityGoalResult(NewGoal("g")); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "sg", type = ServerCommandTypes.SetGoal,
            serverSessionId = ctx.SessionId,
            objective = "g"
        }, ServerJsonSerializer.Options));

        Assert.Null(captured);
    }

    // --- get_goal ---

    [Fact]
    public async Task GetGoal_ReturnsCurrentGoal()
    {
        var goal = NewGoal("Ship it");
        var surface = new FakeContinuitySurface { OnGetGoal = () => new ContinuityGoalResult(goal) };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "gg", type = ServerCommandTypes.GetGoal,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityGoalResult>(response.Data);
        Assert.Equal("Ship it", result.Goal!.Objective);
        Assert.Equal(1, surface.GetGoalCalls);
    }

    [Fact]
    public async Task GetGoal_NoGoal_ReturnsNullGoalInResult()
    {
        var surface = new FakeContinuitySurface { OnGetGoal = () => new ContinuityGoalResult(null) };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "gg", type = ServerCommandTypes.GetGoal,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityGoalResult>(response.Data);
        Assert.Null(result.Goal);
    }

    // --- schedule_job ---

    [Fact]
    public async Task ScheduleJob_InvokesServiceAndReturnsJob()
    {
        string? capName = null, capCron = null, capPrompt = null;
        bool? capEnabled = null;
        var job = NewJob("nightly-build", "0 0 * * *", "run tests");
        var surface = new FakeContinuitySurface
        {
            OnScheduleJob = (name, cron, prompt, enabled) =>
            {
                capName = name; capCron = cron; capPrompt = prompt; capEnabled = enabled;
                return new ContinuityJobResult(job);
            }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "sj", type = ServerCommandTypes.ScheduleJob,
            serverSessionId = ctx.SessionId,
            name = "nightly-build",
            cron = "0 0 * * *",
            prompt = "run tests",
            enabled = true
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityJobResult>(response.Data);
        Assert.Equal("nightly-build", result.Job!.Name);
        Assert.Equal("nightly-build", capName);
        Assert.Equal("0 0 * * *", capCron);
        Assert.Equal("run tests", capPrompt);
        Assert.True(capEnabled);
        Assert.Equal(1, surface.ScheduleJobCalls);
    }

    [Fact]
    public async Task ScheduleJob_NullEnabled_PassesNull()
    {
        bool? captured = false;
        var surface = new FakeContinuitySurface
        {
            OnScheduleJob = (_, _, _, enabled) => { captured = enabled; return new ContinuityJobResult(NewJob("j", "* * * * *", "p")); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "sj", type = ServerCommandTypes.ScheduleJob,
            serverSessionId = ctx.SessionId,
            name = "j", cron = "* * * * *", prompt = "p"
        }, ServerJsonSerializer.Options));

        Assert.Null(captured);
    }

    // --- list_jobs ---

    [Fact]
    public async Task ListJobs_ReturnsAllJobs()
    {
        var jobs = new List<ContinuityJob>
        {
            NewJob("job-a", "@hourly", "a"),
            NewJob("job-b", "@daily", "b")
        };
        var surface = new FakeContinuitySurface { OnListJobs = () => new ContinuityJobListResult(jobs) };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "lj", type = ServerCommandTypes.ListJobs,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsAssignableFrom<ContinuityJobListResult>(response.Data);
        Assert.Equal(2, result.Jobs.Count);
        Assert.Equal("job-a", result.Jobs[0].Name);
        Assert.Equal("job-b", result.Jobs[1].Name);
    }

    [Fact]
    public async Task ListJobs_NoJobs_ReturnsEmptyList()
    {
        var surface = new FakeContinuitySurface { OnListJobs = () => new ContinuityJobListResult([]) };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "lj", type = ServerCommandTypes.ListJobs,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsAssignableFrom<ContinuityJobListResult>(response.Data);
        Assert.Empty(result.Jobs);
    }

    // --- cancel_job ---

    [Fact]
    public async Task CancelJob_InvokesServiceAndReturnsCancelledJob()
    {
        string? captured = null;
        var job = NewJob("to-cancel", "@daily", "x");
        var surface = new FakeContinuitySurface
        {
            OnCancelJob = id => { captured = id; return new ContinuityJobResult(job); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "cj", type = ServerCommandTypes.CancelJob,
            serverSessionId = ctx.SessionId,
            jobId = "abc-123"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityJobResult>(response.Data);
        Assert.Equal("to-cancel", result.Job!.Name);
        Assert.Equal("abc-123", captured);
    }

    [Fact]
    public async Task CancelJob_UnknownJob_ReturnsNullJobInResult()
    {
        var surface = new FakeContinuitySurface { OnCancelJob = _ => new ContinuityJobResult(null) };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "cj", type = ServerCommandTypes.CancelJob,
            serverSessionId = ctx.SessionId,
            jobId = "nonexistent"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityJobResult>(response.Data);
        Assert.Null(result.Job);
    }

    // --- autonomous ---

    [Fact]
    public async Task Autonomous_InvokesServiceAndReturnsStartResult()
    {
        AutonomousCommand? captured = null;
        var state = NewRunState("run-1", "Continue building");
        var surface = new FakeContinuitySurface
        {
            OnStartAutonomous = cmd => { captured = cmd; return new AutonomousStartResult("run-1", state); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "au", type = ServerCommandTypes.Autonomous,
            serverSessionId = ctx.SessionId,
            message = "Continue building",
            maxTurns = 5,
            maxTokens = 10000,
            timeoutMinutes = 15
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<AutonomousStartResult>(response.Data);
        Assert.Equal("run-1", result.RunId);
        Assert.Equal("Continue building", result.State.Instruction);
        Assert.NotNull(captured);
        Assert.Equal("Continue building", captured!.Message);
        Assert.Equal(5, captured.MaxTurns);
        Assert.Equal(10000, captured.MaxTokens);
        Assert.Equal(15, captured.TimeoutMinutes);
    }

    [Fact]
    public async Task Autonomous_NullMessage_PassesNull()
    {
        string? captured = "sentinel";
        var surface = new FakeContinuitySurface
        {
            OnStartAutonomous = cmd => { captured = cmd.Message; return new AutonomousStartResult("r", NewRunState("r", "i")); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "au", type = ServerCommandTypes.Autonomous,
            serverSessionId = ctx.SessionId,
            maxTurns = 3
        }, ServerJsonSerializer.Options));

        Assert.Null(captured);
    }

    [Fact]
    public async Task Autonomous_WithGates_PassesGates()
    {
        AutonomousCommand? captured = null;
        var surface = new FakeContinuitySurface
        {
            OnStartAutonomous = cmd => { captured = cmd; return new AutonomousStartResult("r", NewRunState("r", "i")); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "au", type = ServerCommandTypes.Autonomous,
            serverSessionId = ctx.SessionId,
            message = "work",
            gates = new[] { new { id = "build", command = "dotnet build", timeoutSeconds = 60, retries = 2 } }
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.NotNull(captured);
        Assert.Single(captured!.Gates!);
        Assert.Equal("build", captured.Gates![0].Id);
        Assert.Equal("dotnet build", captured.Gates![0].Command);
        Assert.Equal(60, captured.Gates![0].TimeoutSeconds);
        Assert.Equal(2, captured.Gates![0].Retries);
    }

    // --- get_continuity_state ---

    [Fact]
    public async Task GetContinuityState_ReturnsFullSnapshot()
    {
        var goal = NewGoal("active goal");
        var jobs = new List<ContinuityJob> { NewJob("j1", "@hourly", "p") };
        var run = NewRunState("r1", "continuing");
        var surface = new FakeContinuitySurface
        {
            OnGetState = () => new ContinuityStateResult(goal, jobs, run)
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "gs", type = ServerCommandTypes.GetContinuityState,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityStateResult>(response.Data);
        Assert.Equal("active goal", result.Goal!.Objective);
        Assert.Single(result.Jobs);
        Assert.Equal("r1", result.Run!.RunId);
    }

    [Fact]
    public async Task GetContinuityState_EmptyState_ReturnsAllNulls()
    {
        var surface = new FakeContinuitySurface
        {
            OnGetState = () => new ContinuityStateResult(null, [], null)
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "gs", type = ServerCommandTypes.GetContinuityState,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ContinuityStateResult>(response.Data);
        Assert.Null(result.Goal);
        Assert.Empty(result.Jobs);
        Assert.Null(result.Run);
    }

    // --- state round-trip (set_goal then get_goal then get_continuity_state) ---

    [Fact]
    public async Task StateRoundTrip_SetGoal_GetGoal_GetState_AllConsistent()
    {
        var goal = NewGoal("round-trip objective");
        var jobs = new List<ContinuityJob> { NewJob("rt-job", "@daily", "do work") };
        var surface = new FakeContinuitySurface
        {
            OnSetGoal = (_, _) => new ContinuityGoalResult(goal),
            OnGetGoal = () => new ContinuityGoalResult(goal),
            OnGetState = () => new ContinuityStateResult(goal, jobs, null)
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        // set_goal
        var setResp = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1", type = ServerCommandTypes.SetGoal,
            serverSessionId = ctx.SessionId, objective = "round-trip objective"
        }, ServerJsonSerializer.Options));
        Assert.True(setResp.Success);

        // get_goal
        var getResp = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "2", type = ServerCommandTypes.GetGoal,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));
        Assert.True(getResp.Success);
        Assert.Equal("round-trip objective", Assert.IsType<ContinuityGoalResult>(getResp.Data).Goal!.Objective);

        // get_continuity_state
        var stateResp = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "3", type = ServerCommandTypes.GetContinuityState,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));
        Assert.True(stateResp.Success);
        var state = Assert.IsType<ContinuityStateResult>(stateResp.Data);
        Assert.Equal("round-trip objective", state.Goal!.Objective);
        Assert.Single(state.Jobs);
    }

    // --- schedule/cancel lifecycle ---

    [Fact]
    public async Task ScheduleCancelLifecycle_Schedule_List_Cancel_List_EmptyAfter()
    {
        var job = NewJob("lifecycle-job", "@hourly", "tick");
        var liveJobs = new List<ContinuityJob>();

        var surface = new FakeContinuitySurface
        {
            OnScheduleJob = (_, _, _, _) => { liveJobs.Add(job); return new ContinuityJobResult(job); },
            OnListJobs = () => new ContinuityJobListResult(liveJobs.ToArray()),
            OnCancelJob = id => { liveJobs.RemoveAll(j => j.Id == id); return new ContinuityJobResult(job); }
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        // schedule
        var schedResp = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1", type = ServerCommandTypes.ScheduleJob,
            serverSessionId = ctx.SessionId,
            name = "lifecycle-job", cron = "@hourly", prompt = "tick"
        }, ServerJsonSerializer.Options));
        Assert.True(schedResp.Success);

        // list — should have 1
        var listResp1 = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "2", type = ServerCommandTypes.ListJobs,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));
        Assert.True(listResp1.Success);
        Assert.Single(Assert.IsAssignableFrom<ContinuityJobListResult>(listResp1.Data).Jobs);

        // cancel
        var cancelResp = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "3", type = ServerCommandTypes.CancelJob,
            serverSessionId = ctx.SessionId,
            jobId = job.Id
        }, ServerJsonSerializer.Options));
        Assert.True(cancelResp.Success);

        // list — should be empty
        var listResp2 = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "4", type = ServerCommandTypes.ListJobs,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));
        Assert.True(listResp2.Success);
        Assert.Empty(Assert.IsAssignableFrom<ContinuityJobListResult>(listResp2.Data).Jobs);
    }

    // --- error envelopes ---

    [Fact]
    public async Task AllCommands_WithoutExtension_ReturnsContinuityUnavailable()
    {
        // Runtime has no extension manager / no continuity extension loaded.
        var registry = new ServerSessionRegistry((request, _) => CreatePlainRuntimeAsync(request.Cwd), TimeSpan.FromHours(1));
        var handler = new PiServerWebSocketHandler(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);
        var created = await CreateSessionAsync(handler);

        foreach (var (type, payload) in new[]
        {
            (ServerCommandTypes.SetGoal, (object)new { objective = "x" }),
            (ServerCommandTypes.GetGoal, new { }),
            (ServerCommandTypes.ScheduleJob, new { name = "n", cron = "@daily", prompt = "p" }),
            (ServerCommandTypes.ListJobs, new { }),
            (ServerCommandTypes.CancelJob, new { jobId = "x" }),
            (ServerCommandTypes.Autonomous, new { }),
            (ServerCommandTypes.GetContinuityState, new { })
        })
        {
            var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
            {
                id = "e", type, serverSessionId = created.ServerSessionId, payload
            }, ServerJsonSerializer.Options));
            Assert.False(response.Success);
            Assert.Equal("continuity_unavailable", response.Error?.Code);
        }
    }

    [Fact]
    public async Task SetGoal_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.SetGoal,
            serverSessionId = "nonexistent",
            objective = "g"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task GetGoal_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.GetGoal,
            serverSessionId = "nope"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task CancelJob_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.CancelJob,
            serverSessionId = "missing",
            jobId = "j"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task Autonomous_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.Autonomous,
            serverSessionId = "gone"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task GetContinuityState_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.GetContinuityState,
            serverSessionId = "absent"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task ScheduleJob_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.ScheduleJob,
            serverSessionId = "none",
            name = "n", cron = "@daily", prompt = "p"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task ListJobs_MissingSession_ReturnsCommandFailed()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.ListJobs,
            serverSessionId = "void"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task UnknownCommand_StillReturnsUnknownCommand()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakeContinuitySurface());

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "u", type = "definitely_not_a_real_command",
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("unknown_command", response.Error?.Code);
    }

    // --- helpers ---

    private static ContinuityGoal NewGoal(string objective)
        => new("goal-1", objective, "", ContinuityGoalStatus.Idle, null, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, null);

    private static ContinuityJob NewJob(string name, string cron, string prompt)
        => new("job-" + Guid.NewGuid().ToString("N")[..8], name, cron, prompt, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    private static AutonomousRunState NewRunState(string runId, string instruction)
        => new(runId, null, instruction, 10, null, DateTimeOffset.UtcNow.AddMinutes(30), [], true, 0, 0, DateTimeOffset.UtcNow, null);

    private static async Task<(PiServerWebSocketHandler Handler, string SessionId)> CreateHandlerWithSurfaceAsync(FakeContinuitySurface surface)
    {
        var manager = new ExtensionManager();
        await manager.InitializeAsync(
            new ExtensionDescriptor("pisharp-continuity", "PiSharp Continuity Suite", "0.1.0", SourceId: "pi:extension:continuity"),
            surface,
            new ExtensionRuntimeBinding(TempRoot(), false, NoExtensionUi.Instance));
        var registry = new ServerSessionRegistry((request, ct) => CreateRuntimeAsync(request.Cwd, manager), TimeSpan.FromHours(1));
        var handler = new PiServerWebSocketHandler(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);
        var created = await CreateSessionAsync(handler);
        return (handler, created.ServerSessionId);
    }

    private static async Task<ServerSessionCreated> CreateSessionAsync(PiServerWebSocketHandler handler)
    {
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options));
        return Assert.IsType<ServerSessionCreated>(response.Data);
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root, ExtensionManager manager)
        => await BuildRuntimeAsync(root, manager);

    private static async Task<PiSharp.Runtime.SessionRuntime> CreatePlainRuntimeAsync(string root)
        => await BuildRuntimeAsync(root, manager: null);

    private static async Task<PiSharp.Runtime.SessionRuntime> BuildRuntimeAsync(string root, ExtensionManager? manager)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, BuildHarness, initial, extensionManager: manager);
    }

    private static AgentHarness<JsonlSessionMetadata> BuildHarness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(PiSharp.Abstractions.Messages.AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = PiSharp.Abstractions.Messages.AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-continuity-commands-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>In-memory <see cref="IExtension"/> + <see cref="IContinuitySessionService"/> used to exercise handler dispatch.</summary>
    private sealed class FakeContinuitySurface : IExtension, IContinuitySessionService
    {
        public int SetGoalCalls { get; private set; }
        public int GetGoalCalls { get; private set; }
        public int ScheduleJobCalls { get; private set; }

        public Func<string, long?, ContinuityGoalResult>? OnSetGoal { get; set; }
        public Func<ContinuityGoalResult>? OnGetGoal { get; set; }
        public Func<string, string, string, bool?, ContinuityJobResult>? OnScheduleJob { get; set; }
        public Func<ContinuityJobListResult>? OnListJobs { get; set; }
        public Func<string, ContinuityJobResult>? OnCancelJob { get; set; }
        public Func<AutonomousCommand, AutonomousStartResult>? OnStartAutonomous { get; set; }
        public Func<ContinuityStateResult>? OnGetState { get; set; }

        public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ContinuityGoalResult> SetGoalAsync(string objective, long? maxTokens, CancellationToken ct)
        {
            SetGoalCalls++;
            return Task.FromResult(OnSetGoal?.Invoke(objective, maxTokens) ?? new ContinuityGoalResult(null));
        }

        public Task<ContinuityGoalResult> GetGoalAsync(CancellationToken ct)
        {
            GetGoalCalls++;
            return Task.FromResult(OnGetGoal?.Invoke() ?? new ContinuityGoalResult(null));
        }

        public Task<ContinuityJobResult> ScheduleJobAsync(string name, string cron, string prompt, bool? enabled, CancellationToken ct)
        {
            ScheduleJobCalls++;
            return Task.FromResult(OnScheduleJob?.Invoke(name, cron, prompt, enabled) ?? new ContinuityJobResult(null));
        }

        public Task<ContinuityJobListResult> ListJobsAsync(CancellationToken ct)
            => Task.FromResult(OnListJobs?.Invoke() ?? new ContinuityJobListResult([]));

        public Task<ContinuityJobResult> CancelJobAsync(string jobId, CancellationToken ct)
            => Task.FromResult(OnCancelJob?.Invoke(jobId) ?? new ContinuityJobResult(null));

        public Task<AutonomousStartResult> StartAutonomousAsync(AutonomousCommand cmd, CancellationToken ct)
            => Task.FromResult(OnStartAutonomous?.Invoke(cmd) ?? new AutonomousStartResult("run-0", NewRunState("run-0", "noop")));

        public Task<ContinuityStateResult> GetStateAsync(CancellationToken ct)
            => Task.FromResult(OnGetState?.Invoke() ?? new ContinuityStateResult(null, [], null));
    }
}
