using System.Text.Json;
using PiSharp.Agent.Core.Events;
using PiSharp.Client;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Serialization;
using Xunit;

namespace PiSharp.Client.Tests;

/// <summary>
/// Real client ↔ in-process daemon integration. A live <see cref="PiServerHost"/> runs on an
/// ephemeral port; the client side uses the real <see cref="ClientWebSocketTransport"/> and
/// <see cref="ClientSessionConnection"/>. No LLM/provider is available (the host builds its own
/// runtime via the real bootstrap), so event-producing actions are driven through the
/// <c>run_command</c> host delegate, which either emits a server event directly or triggers a
/// <c>ServerUiBridge</c> round trip that the client completes with a <c>ui_response</c>.
/// </summary>
public sealed class DaemonIntegrationTests
{
    private const string ApiKey = "itest-key";

    [Fact]
    public async Task RunCommand_EmitsEvent_StreamedToClientOverRealTransport()
    {
        var root = NewTempDir();
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = (context, text, options, ct) =>
            {
                context.Session.EmitEvent(AgentSessionEvent.FromServer("system_message", new { text }));
                return Task.FromResult(new ServerCommandResult(true, text));
            },
        });
        await host.StartAsync(0);

        var transport = new ClientWebSocketTransport(TimeSpan.FromSeconds(30));
        await using var conn = new ClientSessionConnection(transport);
        await conn.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), ApiKey, CancellationToken.None);

        var createResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            CreatePayload(root),
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);
        var sessionId = ((JsonElement)createResp.Data!).GetProperty("serverSessionId").GetString()!;

        await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: sessionId),
            new { sinceSequence = 0L },
            CancellationToken.None);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.EventReceived += envelope =>
        {
            if (envelope.Event.Type == "system_message")
                received.TrySetResult(ClientToTuiAdapter.FromPayload<TextPayload>(envelope.Event.Data)?.Text ?? string.Empty);
        };

        var runResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.RunCommand, ServerSessionId: sessionId),
            new { text = "ping", options = (object?)null },
            CancellationToken.None);
        Assert.True(runResp.Success, runResp.Error?.Message);

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("ping", text);
    }

    [Fact]
    public async Task RunCommand_ExecutesDaemonSide_WithSelectRoundTrip()
    {
        var root = NewTempDir();
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            // The daemon-side delegate triggers a UI request bridged to the attached client and
            // awaits its response before run_command completes — the full select round trip.
            RunCommandAsync = async (context, text, options, ct) =>
            {
                var intent = new ServerUiIntent("select-1", "select", "Pick one", "Choose an option", ["a", "b"], null);
                var uiResponse = await context.UiBridge.RequestUiAsync(intent, ct);
                return new ServerCommandResult(true, uiResponse.Cancelled ? null : uiResponse.Value?.ToString());
            },
        });
        await host.StartAsync(0);

        var transport = new ClientWebSocketTransport(TimeSpan.FromSeconds(30));
        await using var conn = new ClientSessionConnection(transport);
        await conn.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), ApiKey, CancellationToken.None);

        var createResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            CreatePayload(root),
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);
        var sessionId = ((JsonElement)createResp.Data!).GetProperty("serverSessionId").GetString()!;

        await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: sessionId),
            new { sinceSequence = 0L },
            CancellationToken.None);

        var uiRequestTcs = new TaskCompletionSource<ServerUiIntent>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.EventReceived += envelope =>
        {
            if (envelope.Event.Type == "ui_request")
                uiRequestTcs.TrySetResult(ClientToTuiAdapter.FromPayload<ServerUiIntent>(envelope.Event.Data)!);
        };

        // run_command awaits a UI response, so it stays in flight until the client answers.
        var runTask = conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.RunCommand, ServerSessionId: sessionId),
            new { text = "/select", options = (object?)null },
            CancellationToken.None);

        var intent = await uiRequestTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("select", intent.Kind);
        Assert.NotNull(intent.Options);
        Assert.Equal(new[] { "a", "b" }, intent.Options!.ToArray());

        // Complete the round trip: the client answers the select with value "a".
        await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.UiResponse, ServerSessionId: sessionId),
            new { requestId = intent.RequestId, value = "a", cancelled = false },
            CancellationToken.None);

        var runResp = await runTask;
        Assert.True(runResp.Success, runResp.Error?.Message);
        var result = ClientToTuiAdapter.FromPayload<ServerCommandResult>(runResp.Data);
        Assert.NotNull(result);
        Assert.True(result!.Handled);
        Assert.Equal("a", result.Message);
    }

    [Fact]
    public async Task CreateSession_DefaultPayload_Succeeds_OverRealTransport()
    {
        // The InteractiveMode default create_session payload: cwd only, with none of the
        // noTools/noExtensions/noSkills/noPromptTemplates/noThemes/noContextFiles suppression
        // flags set. Default startup still runs extension discovery (observed ~23s), so the
        // transport is given a generous command timeout rather than a short arbitrary one.
        var root = NewTempDir();
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
        });
        await host.StartAsync(0);

        var transport = new ClientWebSocketTransport(TimeSpan.FromSeconds(90));
        await using var conn = new ClientSessionConnection(transport);
        await conn.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), ApiKey, CancellationToken.None);

        // Explicit envelope id proves wire correlation: the real transport resolves the matching
        // ServerResponse by id and the server echoes it back.
        var createResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession, Id: "itest-create-default"),
            new { cwd = root },
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);
        Assert.Equal("itest-create-default", createResp.Id);

        // Round-trip the wire payload through the server JSON options into the typed contract.
        var created = JsonSerializer.Deserialize<ServerSessionCreated>(
            JsonSerializer.Serialize(createResp.Data, ServerJsonSerializer.Options),
            ServerJsonSerializer.Options);
        Assert.NotNull(created);
        Assert.False(string.IsNullOrEmpty(created!.ServerSessionId));
        Assert.NotNull(created.State);
        Assert.Equal(created.ServerSessionId, created.State.ServerSessionId);
        Assert.Equal(root, created.State.Cwd);

        // Post-create remote startup sequence driven through the real backend on the same
        // connection, mirroring what InteractiveMode runs after create_session.
        await using var backend = new RemoteTuiBackend(conn)
        {
            ServerSessionId = created.ServerSessionId,
        };
        Assert.Empty(await backend.GetStartupMessagesAsync()); // default host has no delegate
        Assert.Null(await backend.GetThemeAsync()); // default host has no theme

        var sessionName = await backend.GetSessionNameAsync();
        var snapshot = await backend.GetSessionSnapshotAsync();
        Assert.Equal(created.State.RuntimeSessionId, snapshot.SessionId);
        Assert.Equal(created.State.RuntimeSessionPath, snapshot.SessionFile);
        Assert.Equal(sessionName, snapshot.SessionName);
    }

    [Fact]
    public async Task PostStartupChecks_EmitsSystemMessage_DeliveredToHarnessListener()
    {
        var root = NewTempDir();
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            PostStartupChecksAsync = (session, emit, ct) =>
            {
                emit("Self-update check complete");
                return Task.CompletedTask;
            },
        });
        await host.StartAsync(0);

        var transport = new ClientWebSocketTransport(TimeSpan.FromSeconds(30));
        await using var conn = new ClientSessionConnection(transport);
        await conn.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), ApiKey, CancellationToken.None);

        var createResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            CreatePayload(root),
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);
        var sessionId = ((JsonElement)createResp.Data!).GetProperty("serverSessionId").GetString()!;

        await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: sessionId),
            new { sinceSequence = 0L },
            CancellationToken.None);

        await using var backend = new RemoteTuiBackend(conn)
        {
            ServerSessionId = sessionId,
        };

        var received = new TaskCompletionSource<AgentHarnessOwnEvent.SystemMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = backend.Subscribe((evt, ct) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SystemMessage system })
                received.TrySetResult(system);
            return Task.CompletedTask;
        });

        // Drive post_startup_checks: the server delegate emits one system_message event through
        // the real transport; the harness listener receives the mapped Own(SystemMessage) event.
        await backend.PostStartupChecksAsync(_ => Task.CompletedTask);

        var system = await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("Self-update check complete", system.Text);
        Assert.False(system.IsError);
    }

    [Fact]
    public async Task PostStartupChecks_WithoutDelegate_ReturnsNotAvailable_DoesNotThrow()
    {
        var root = NewTempDir();
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
        });
        await host.StartAsync(0);

        var transport = new ClientWebSocketTransport(TimeSpan.FromSeconds(30));
        await using var conn = new ClientSessionConnection(transport);
        await conn.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), ApiKey, CancellationToken.None);

        var createResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            CreatePayload(root),
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);
        var sessionId = ((JsonElement)createResp.Data!).GetProperty("serverSessionId").GetString()!;

        await using var backend = new RemoteTuiBackend(conn)
        {
            ServerSessionId = sessionId,
        };

        // A delegate-less daemon answers not_available; the client must tolerate it instead of throwing.
        await backend.PostStartupChecksAsync(_ => Task.CompletedTask);
    }

    // --- helpers ---

    private static object CreatePayload(string root) => new
    {
        cwd = root,
        sessionsRoot = Path.Combine(root, "sessions"),
        noTools = true,
        noBuiltinTools = true,
        noExtensions = true,
        noSkills = true,
        noPromptTemplates = true,
        noThemes = true,
        noContextFiles = true,
    };

    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-daemon-itest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Matches the <c>{ text = ... }</c> body of a <c>system_message</c> server event.</summary>
    private sealed record TextPayload(string? Text);
}
