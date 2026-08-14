using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using Xunit;

namespace PiSharp.Sdk.Tests;

public sealed class SessionConnectionTests
{
    [Fact]
    public async Task Attach_RunCommand_StreamsEventsAndUpdatesTranscript()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = (context, text, options, ct) =>
            {
                EmitMessage(context, text);
                return Task.FromResult(new ServerCommandResult(true, text));
            },
        });
        var created = await host.CreateSessionAsync("sdk-attach-1");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        var result = await session.RunCommandAsync("ping");
        Assert.NotNull(result);
        Assert.True(result!.Handled);
        Assert.Equal("ping", result.Message);

        // The flat stream carries the emitted message events (replay then live).
        var events = new List<AgentSessionEvent>();
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var e in session.WithEvents(streamCts.Token))
        {
            if (e.Type is "message_start" or "message_end")
            {
                events.Add(e);
                if (events.Count == 2) break;
            }
        }

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Contains("message_", e.Type));

        // The reducer folded them into the read-only transcript view.
        var row = await WaitForAsync(
            () => session.State.TranscriptItems.FirstOrDefault(r => r.Kind == ClientMessageRowKind.User),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(row);
    }

    [Fact]
    public async Task Changed_RaisesAfterAppliedEvents()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = (context, text, options, ct) =>
            {
                EmitMessage(context, text);
                return Task.FromResult(new ServerCommandResult(true, text));
            },
        });
        var created = await host.CreateSessionAsync("sdk-attach-2");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        var raised = new TaskCompletionSource<ClientSessionChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, args) =>
        {
            if (args.Applied.Any(e => e.Type == "message_start")) raised.TrySetResult(args);
        };

        await session.RunCommandAsync("hi");
        var args = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(args.LastAppliedSequence > 0);
        Assert.True(args.HeadSequence >= args.LastAppliedSequence);
    }

    [Fact]
    public async Task Reattach_ReplaysRetainedEvents()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = (context, text, options, ct) =>
            {
                EmitMessage(context, text);
                return Task.FromResult(new ServerCommandResult(true, text));
            },
        });
        var created = await host.CreateSessionAsync("sdk-attach-3");

        // Emit retained events before any attach.
        var control = await host.Client.AttachAsync(created.ServerSessionId);
        await control.RunCommandAsync("first");
        await control.DisposeAsync();
        // A fresh attach replays the retained tail by default (head - ReplayWindow).
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);
        var row = await WaitForAsync(
            () => session.State.TranscriptItems.FirstOrDefault(r => r.Kind == ClientMessageRowKind.User),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(row);

        var replayed = new List<AgentSessionEvent>();
        using var reattachCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var e in session.WithEvents(reattachCts.Token))
        {
            if (e.Type == "message_end") replayed.Add(e);
            if (replayed.Count > 0 && session.State.LastAppliedSequence >= session.State.HeadSequence)
            {
                break;
            }
        }

        Assert.NotEmpty(replayed);
    }

    [Fact]
    public async Task UiRequest_AutoDecline_ResolvesCancelled()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = async (context, text, options, ct) =>
            {
                var intent = new ServerUiIntent("select-1", "select", "Pick one", "Choose an option", ["a", "b"], null);
                var uiResponse = await context.UiBridge.RequestUiAsync(intent, ct);
                return new ServerCommandResult(true, uiResponse.Cancelled ? null : uiResponse.Value?.ToString());
            },
        });
        var created = await host.CreateSessionAsync("sdk-ui-1");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId, new AttachOptions { AutoHandleUiRequests = true });

        var result = await session.RunCommandAsync("/select");
        Assert.NotNull(result);
        Assert.True(result!.Handled);
        Assert.Null(result.Message); // auto-declined
    }

    [Fact]
    public async Task UiRequest_Handler_AnswersWithValue()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = async (context, text, options, ct) =>
            {
                var intent = new ServerUiIntent("select-2", "select", "Pick one", "Choose an option", ["a", "b"], null);
                var uiResponse = await context.UiBridge.RequestUiAsync(intent, ct);
                return new ServerCommandResult(true, uiResponse.Cancelled ? null : uiResponse.Value?.ToString());
            },
        });
        var created = await host.CreateSessionAsync("sdk-ui-2");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        string? seenKind = null;
        session.SetUiRequestHandler((request, ct) =>
        {
            seenKind = request.Kind;
            return Task.FromResult(new UiResponse(request.RequestId, "b"));
        });

        var result = await session.RunCommandAsync("/select");
        Assert.NotNull(result);
        Assert.True(result!.Handled);
        Assert.Equal("select", seenKind);
        Assert.Equal("b", result.Message);
    }

    [Fact]
    public async Task UiRequest_HandlerInstalledAfterRequest_DrainsQueue()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = async (context, text, options, ct) =>
            {
                var intent = new ServerUiIntent("select-3", "select", "Pick one", "Choose an option", ["a", "b"], null);
                var uiResponse = await context.UiBridge.RequestUiAsync(intent, ct);
                return new ServerCommandResult(true, uiResponse.Cancelled ? null : uiResponse.Value?.ToString());
            },
        });
        var created = await host.CreateSessionAsync("sdk-ui-3");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        // No handler yet: the request queues. Run the command, let the request arrive, then install.
        var runTask = session.RunCommandAsync("/select");
        await Task.Delay(250);

        session.SetUiRequestHandler((request, ct) => Task.FromResult(new UiResponse(request.RequestId, "c")));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.NotNull(result);
        Assert.True(result!.Handled);
        Assert.Equal("c", result.Message);
    }

    [Fact]
    public async Task SetSessionName_UpdatesState()
    {
        await using var host = await SdkTestHost.StartAsync();
        var created = await host.CreateSessionAsync("sdk-name");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        await session.SetSessionNameAsync("my session");

        var view = await WaitForAsync(
            () => session.State.SessionName is "my session" ? session.State : null,
            TimeSpan.FromSeconds(5));
        Assert.NotNull(view);
        Assert.Equal("my session", view!.SessionName);
    }

    [Fact]
    public async Task GetSessionSnapshot_ReturnsSnapshot()
    {
        await using var host = await SdkTestHost.StartAsync();
        var created = await host.CreateSessionAsync("sdk-snapshot");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        var snapshot = await session.GetSessionSnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.Equal(created.State.RuntimeSessionId, snapshot!.SessionId);
        Assert.Equal(created.State.RuntimeSessionPath, snapshot.SessionFile);
    }

    [Fact]
    public async Task WaitForIdle_Completes()
    {
        await using var host = await SdkTestHost.StartAsync(new PiServerHostOptions
        {
            ApiKey = SdkTestHost.ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            RunCommandAsync = (context, text, options, ct) => Task.FromResult(new ServerCommandResult(true, text)),
        });
        var created = await host.CreateSessionAsync("sdk-idle");
        await using var session = await host.Client.AttachAsync(created.ServerSessionId);

        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.State.IsBusy);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        await using var host = await SdkTestHost.StartAsync();
        var created = await host.CreateSessionAsync("sdk-dispose");

        var session = await host.Client.AttachAsync(created.ServerSessionId);
        await session.DisposeAsync();
        await session.DisposeAsync(); // second dispose must not throw
        await host.Client.DisposeAsync();
        await host.Client.DisposeAsync();
    }
    private static void EmitMessage(PiServerHostContext context, string text)
    {
        var message = AgentMessages.User(text);
        context.Session.EmitEvent(AgentSessionEvent.FromCore(new AgentEvent.MessageStart(message)));
        context.Session.EmitEvent(AgentSessionEvent.FromCore(new AgentEvent.MessageEnd(message)));
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value) return value;
            await Task.Delay(50);
        }

        return null;
    }
}
