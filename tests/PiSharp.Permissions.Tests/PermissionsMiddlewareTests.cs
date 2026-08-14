using System.Text.Json;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;
using PiSharp.Permissions;
using PiSharp.Permissions.Tests.Fakes;
using Xunit;

namespace PiSharp.Permissions.Tests;

public sealed class PermissionsMiddlewareTests
{
    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private static async Task<(PermissionsTestApi Api, ExtensionMiddleware Middleware)> CreateGateAsync(
        Action<PermissionsTestApi>? configure = null)
    {
        var api = new PermissionsTestApi
        {
            Cwd = "C:/project",
            SessionName = "session-abcdefgh",
            HasUi = false,
            ExecutionEnv = new FakeExecutionEnv { Cwd = "C:/project" }
        };
        configure?.Invoke(api);
        var extension = new PermissionsExtension();
        await extension.InitializeAsync(api);
        return (api, api.RegisteredMiddlewares.Single());
    }

    private static async Task<ExtensionMiddlewareContext> RunAsync(
        ExtensionMiddleware middleware,
        string tool,
        JsonElement args)
    {
        var context = MiddlewareContextBuilder.Before(tool, args);
        await middleware(context, (_, _) => Task.CompletedTask, CancellationToken.None);
        return context;
    }

    private static async Task<(ExtensionMiddlewareContext Context, bool NextCalled)> RunWithNextFlagAsync(
        ExtensionMiddleware middleware,
        string tool,
        JsonElement args)
    {
        var called = false;
        var context = MiddlewareContextBuilder.Before(tool, args);
        await middleware(context, (_, _) => { called = true; return Task.CompletedTask; }, CancellationToken.None);
        return (context, called);
    }

    // --- Phase 2: middleware enforcement ---

    [Fact]
    public async Task AllowListedBash_Executes()
    {
        var (api, middleware) = await CreateGateAsync(api => api.Settings.SetAsync("allow",
            new List<PermissionRule> { new("bash") }).Wait());

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hello" }));

        Assert.False(context.Blocked);
        Assert.True(nextCalled);
        Assert.Empty(api.AuditEntries);
    }

    [Fact]
    public async Task DenyListedBash_BlocksWithReason_AndSkipsNext()
    {
        var (api, middleware) = await CreateGateAsync(api => api.Settings.SetAsync("deny",
            new List<PermissionRule> { new("bash") }).Wait());

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hello" }));

        Assert.True(context.Blocked);
        Assert.False(nextCalled);
        Assert.Contains("Permission", context.BlockReason);
        var audit = Assert.Single(api.AuditEntries);
        Assert.Equal("permission", audit.CustomType);
        Assert.Single(api.ClientEvents, e => e.Name == AuditRecorder.EventBlocked);
    }

    [Fact]
    public async Task ReadOnlyDefault_Allows()
    {
        var (_, middleware) = await CreateGateAsync();

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "read", Args(new { path = "a.txt" }));

        Assert.False(context.Blocked);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OutsideCwdWrite_Blocks()
    {
        var (api, middleware) = await CreateGateAsync();

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "write", Args(new { path = "../outside.txt" }));

        Assert.True(context.Blocked);
        Assert.False(nextCalled);
        Assert.Contains("outside", context.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverwriteWrite_AsksAndHeadlessDenies()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        env.AddExistingFile("C:/project/existing.txt");
        var (_, middleware) = await CreateGateAsync(api => api.ExecutionEnv = env);

        var context = await RunAsync(middleware, "write", Args(new { path = "existing.txt" }));

        Assert.True(context.Blocked);
        Assert.Contains("overwrit", context.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditOff_BlockedCall_WritesNoEntries()
    {
        var (api, middleware) = await CreateGateAsync(api =>
        {
            api.Settings.SetAsync("audit", false).Wait();
            api.Settings.SetAsync("deny", new List<PermissionRule> { new("bash") }).Wait();
        });

        var context = await RunAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.True(context.Blocked);
        Assert.Empty(api.AuditEntries);
        Assert.Empty(api.ClientEvents);
    }

    [Fact]
    public async Task AfterToolCallMiddleware_Only_PassesThrough()
    {
        var (_, middleware) = await CreateGateAsync();
        var context = MiddlewareContextBuilder.After("bash", Args(new { command = "echo hi" }));
        await middleware(context, (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.False(context.Blocked);
    }

    // --- Phase 3: approval flow + grants ---

    [Fact]
    public async Task Ask_ApprovalAllow_Proceeds()
    {
        var ui = new FakeApprovalUi();
        ui.EnqueueVerdict("allow");
        var (api, middleware) = await CreateGateAsync(a =>
        {
            a.HasUi = true;
            a.Ui = ui;
        });

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.False(context.Blocked);
        Assert.True(nextCalled);
        Assert.Single(ui.Requests);
        Assert.Single(api.ClientEvents, e => e.Name == AuditRecorder.EventApproved);
    }

    [Fact]
    public async Task Ask_ApprovalDeny_Blocks()
    {
        var ui = new FakeApprovalUi();
        ui.EnqueueVerdict("deny");
        var (api, middleware) = await CreateGateAsync(a =>
        {
            a.HasUi = true;
            a.Ui = ui;
        });

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.True(context.Blocked);
        Assert.False(nextCalled);
        Assert.Contains("denied", context.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(api.ClientEvents, e => e.Name == AuditRecorder.EventBlocked);
    }

    [Fact]
    public async Task Ask_AllowSession_StoresGrant_AndSubsequentCallSkipsPrompt()
    {
        var ui = new FakeApprovalUi();
        ui.EnqueueVerdict("allow_session");
        var (api, middleware) = await CreateGateAsync(a =>
        {
            a.HasUi = true;
            a.Ui = ui;
        });

        var (first, firstNext) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hi" }));
        Assert.False(first.Blocked);
        Assert.True(firstNext);

        var grant = await new GrantStore(api.State).FindAsync("bash", "session-abcdefgh");
        Assert.NotNull(grant);
        Assert.Equal(PermissionAction.Allow, grant!.Action);
        Assert.True(grant.ExpiresAt > DateTimeOffset.UtcNow);

        var (second, secondNext) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hi" }));
        Assert.False(second.Blocked);
        Assert.True(secondNext);
        Assert.Single(ui.Requests); // no second prompt
    }

    [Fact]
    public async Task StoredDenyGrant_BlocksImmediately_WithoutPrompt()
    {
        var ui = new FakeApprovalUi();
        var (api, middleware) = await CreateGateAsync(a =>
        {
            a.HasUi = true;
            a.Ui = ui;
        });
        await new GrantStore(api.State).StoreAsync(new PermissionGrant(
            "session-abcdefgh", "bash", null, PermissionAction.Deny, DateTimeOffset.UtcNow.AddMinutes(30)));

        var context = await RunAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.True(context.Blocked);
        Assert.Contains("grant", context.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ui.Requests);
    }

    [Fact]
    public async Task ExpiredGrant_FallsThroughToPolicy()
    {
        var (api, middleware) = await CreateGateAsync();
        await new GrantStore(api.State).StoreAsync(new PermissionGrant(
            "session-abcdefgh", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(-1)));

        // Headless prompt-mode: expired allow grant must not permit; ask auto-denies.
        var context = await RunAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.True(context.Blocked);
    }

    [Fact]
    public async Task GrantWithPattern_OnlyAllowsMatchingCalls()
    {
        var (api, middleware) = await CreateGateAsync();
        await new GrantStore(api.State).StoreAsync(new PermissionGrant(
            "session-abcdefgh", "bash", "git status.*", PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));
        var (matching, matchingNext) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "git status --short" }));
        Assert.False(matching.Blocked);
        Assert.True(matchingNext);
        var (other, otherNext) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "git push origin" }));
        Assert.True(other.Blocked);
        Assert.False(otherNext);
    }

    // --- Headless posture ---

    [Fact]
    public async Task Headless_AskAutoDenies_WithoutPrompt()
    {
        var (api, middleware) = await CreateGateAsync(); // HasUi=false, headlessDeny default true

        var context = await RunAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.True(context.Blocked);
        Assert.Contains("headless", context.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(api.ClientEvents, e => e.Name == AuditRecorder.EventApproved); // no approval prompt happened
    }

    [Fact]
    public async Task HeadlessDenyDisabled_AllowsHeadlessAsk()
    {
        var (_, middleware) = await CreateGateAsync(api => api.Settings.SetAsync("headlessDeny", false).Wait());
        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.False(context.Blocked);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task AutomaticMode_AllowsAsk_WithoutPrompt()
    {
        var ui = new FakeApprovalUi();
        var (_, middleware) = await CreateGateAsync(a =>
        {
            a.HasUi = true;
            a.Ui = ui;
            a.Settings.SetAsync("mode", PermissionsPolicy.ModeAutomatic).Wait();
        });

        var (context, nextCalled) = await RunWithNextFlagAsync(middleware, "bash", Args(new { command = "echo hi" }));

        Assert.False(context.Blocked);
        Assert.True(nextCalled);
        Assert.Empty(ui.Requests);
    }

    [Fact]
    public async Task StrictMode_BlocksUnlistedTool()
    {
        var (_, middleware) = await CreateGateAsync(api =>
        {
            api.Settings.SetAsync("mode", PermissionsPolicy.ModeStrict).Wait();
            api.Settings.SetAsync("allow", new List<PermissionRule> { new("read") }).Wait();
        });

        var context = await RunAsync(middleware, "write", Args(new { path = "C:/project/new.txt" }));

        Assert.True(context.Blocked);
        Assert.Contains("strict", context.BlockReason, StringComparison.OrdinalIgnoreCase);
    }
}
