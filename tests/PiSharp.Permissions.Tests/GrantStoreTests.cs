using PiSharp.Permissions;
using PiSharp.Permissions.Tests.Fakes;
using Xunit;

namespace PiSharp.Permissions.Tests;

public sealed class GrantStoreTests
{
    private static GrantStore CreateStore(PermissionsTestApi.InMemoryStateApi? state = null)
        => new(state ?? new PermissionsTestApi.InMemoryStateApi());

    [Fact]
    public async Task StoreAndFind_RoundTripsAllowGrant()
    {
        var state = new PermissionsTestApi.InMemoryStateApi();
        var store = CreateStore(state);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var grant = new PermissionGrant("session-abcdefgh", "bash", null, PermissionAction.Allow, expiresAt);

        await store.StoreAsync(grant);
        var found = await store.FindAsync("bash", "session-abcdefgh");

        Assert.NotNull(found);
        Assert.Equal(PermissionAction.Allow, found!.Action);
        Assert.Equal(expiresAt, found.ExpiresAt);
        Assert.Equal("session-abcdefgh", found.SessionKey);
    }

    [Fact]
    public async Task Find_ReturnsDenyGrant()
    {
        var store = CreateStore();
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Deny, DateTimeOffset.UtcNow.AddMinutes(30)));

        var found = await store.FindAsync("bash", "s1");

        Assert.NotNull(found);
        Assert.Equal(PermissionAction.Deny, found!.Action);
    }

    [Fact]
    public async Task Find_AllowTakesPrecedenceOverDeny()
    {
        var store = CreateStore();
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Deny, DateTimeOffset.UtcNow.AddMinutes(30)));
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));

        var found = await store.FindAsync("bash", "s1");

        Assert.Equal(PermissionAction.Allow, found!.Action);
    }

    [Fact]
    public async Task Find_NoGrantForTool_ReturnsNull()
    {
        var store = CreateStore();
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));

        Assert.Null(await store.FindAsync("write", "s1"));
        Assert.Null(await store.FindAsync("bash", "other-session"));
    }

    [Fact]
    public async Task Find_ExpiredGrant_IsPrunedAndReturnsNull()
    {
        var state = new PermissionsTestApi.InMemoryStateApi();
        var store = CreateStore(state);
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null(await store.FindAsync("bash", "s1"));
        Assert.DoesNotContain(await state.ListKeysAsync(), key => key.StartsWith("grant.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revoke_RemovesAllowAndDenyGrants()
    {
        var state = new PermissionsTestApi.InMemoryStateApi();
        var store = CreateStore(state);
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Deny, DateTimeOffset.UtcNow.AddMinutes(30)));

        await store.RevokeAsync("bash", "s1");

        Assert.Null(await store.FindAsync("bash", "s1"));
        Assert.Empty(await state.ListKeysAsync());
    }

    [Fact]
    public async Task List_NarrowsBySessionAndPrunesExpired()
    {
        var state = new PermissionsTestApi.InMemoryStateApi();
        var store = CreateStore(state);
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));
        await store.StoreAsync(new PermissionGrant("s2", "write", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));
        await store.StoreAsync(new PermissionGrant("s3", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var forSession = await store.ListAsync("s1");
        Assert.Single(forSession);
        Assert.Equal("bash", forSession[0].Tool);

        var all = await store.ListAsync();
        Assert.Equal(2, all.Count); // expired s3/bash pruned
    }

    [Fact]
    public async Task Clear_RemovesOnlyGrantKeys()
    {
        var state = new PermissionsTestApi.InMemoryStateApi();
        await state.SetAsync("unrelated", "keep-me");
        var store = CreateStore(state);
        await store.StoreAsync(new PermissionGrant("s1", "bash", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));

        await store.ClearAsync();

        Assert.Empty(await store.ListAsync());
        Assert.Equal("keep-me", await state.GetAsync("unrelated"));
    }

    [Fact]
    public void KeyFor_SanitizesToolAndSession()
    {
        var key = GrantStore.KeyFor(PermissionAction.Allow, "Bash", "session/with:weird chars");

        Assert.StartsWith("grant.allow.", key);
        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain(':', key);
    }

    [Fact]
    public async Task RoundTrip_DottedToolName_PreservesFullToolName()
    {
        var store = CreateStore();
        var grant = new PermissionGrant("session-1", "mcp.foo.read", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30));

        await store.StoreAsync(grant);
        var found = await store.FindAsync("mcp.foo.read", "session-1");

        Assert.NotNull(found);
        Assert.Equal(PermissionAction.Allow, found!.Action);
        Assert.Equal("mcp.foo.read", found.Tool);
        Assert.Equal("session-1", found.SessionKey);
    }

    [Fact]
    public async Task List_DottedToolNames_RoundTripsToolAndSession()
    {
        var store = CreateStore();
        await store.StoreAsync(new PermissionGrant("session-1", "mcp.foo.read", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));
        await store.StoreAsync(new PermissionGrant("session-1", "mcp.foo.write", null, PermissionAction.Allow, DateTimeOffset.UtcNow.AddMinutes(30)));

        var listed = await store.ListAsync("session-1");

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, grant => grant.Tool == "mcp.foo.read" && grant.SessionKey == "session-1");
        Assert.Contains(listed, grant => grant.Tool == "mcp.foo.write" && grant.SessionKey == "session-1");
    }

    [Fact]
    public void KeyFor_DottedTool_EmitsVersionedEncodedKey()
    {
        var key = GrantStore.KeyFor(PermissionAction.Allow, "mcp.foo.read", "session-1");

        Assert.StartsWith("grant.allow.v2.", key);
    }

    [Fact]
    public async Task Find_LegacyFormatKey_RemainsReadable()
    {
        var state = new PermissionsTestApi.InMemoryStateApi();
        var store = CreateStore(state);
        await state.SetAsync("grant.allow.bash.sess1", new GrantStore.StoredGrant(null, DateTimeOffset.UtcNow.AddMinutes(30)));

        var found = await store.FindAsync("bash", "sess1");

        Assert.NotNull(found);
        Assert.Equal(PermissionAction.Allow, found!.Action);
        Assert.Equal("bash", found.Tool);
        Assert.Equal("sess1", found.SessionKey);
    }
}
