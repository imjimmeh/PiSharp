using System.Text.Json;
using PiSharp.Permissions;
using PiSharp.Permissions.Tests.Fakes;
using Xunit;

namespace PiSharp.Permissions.Tests;

public sealed class ApprovalClientTests
{
    private static JsonElement Args => JsonSerializer.SerializeToElement(new { command = "git push origin" });

    [Fact]
    public async Task Headless_ReturnsDeny_WithoutTouchingUi()
    {
        var api = new PermissionsTestApi { HasUi = false, Ui = new FakeApprovalUi() };
        var client = new ApprovalClient(api);

        var verdict = await client.RequestApprovalAsync("bash", Args, "reason", "s1");

        Assert.Equal(ApprovalVerdict.Deny, verdict);
        Assert.Empty(((FakeApprovalUi)api.Ui).Requests);
    }

    [Theory]
    [InlineData("allow", ApprovalVerdict.Allow)]
    [InlineData("allow_session", ApprovalVerdict.AllowSession)]
    [InlineData("deny", ApprovalVerdict.Deny)]
    public async Task UserVerdicts_MapToApprovalVerdicts(string value, ApprovalVerdict expected)
    {
        var ui = new FakeApprovalUi();
        ui.EnqueueVerdict(value);
        var api = new PermissionsTestApi { HasUi = true, Ui = ui };
        var client = new ApprovalClient(api);

        var verdict = await client.RequestApprovalAsync("bash", Args, "reason", "s1");

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public async Task UnknownVerdict_DefaultsToDeny()
    {
        var ui = new FakeApprovalUi();
        ui.EnqueueVerdict("maybe");
        var api = new PermissionsTestApi { HasUi = true, Ui = ui };
        var client = new ApprovalClient(api);

        var verdict = await client.RequestApprovalAsync("bash", Args, "reason", "s1");

        Assert.Equal(ApprovalVerdict.Deny, verdict);
    }

    [Fact]
    public async Task FailedRequest_DefaultsToDeny()
    {
        var api = new PermissionsTestApi { HasUi = true, Ui = new FakeApprovalUi() }; // no verdict enqueued → failed result
        var client = new ApprovalClient(api);

        var verdict = await client.RequestApprovalAsync("bash", Args, "reason", "s1");

        Assert.Equal(ApprovalVerdict.Deny, verdict);
    }

    [Fact]
    public async Task ThrowingUi_DefaultsToDeny()
    {
        var api = new PermissionsTestApi { HasUi = true, Ui = ThrowingExtensionUi.Instance };
        var client = new ApprovalClient(api);

        var verdict = await client.RequestApprovalAsync("bash", Args, "reason", "s1");

        Assert.Equal(ApprovalVerdict.Deny, verdict);
    }

    [Fact]
    public async Task Request_CarriesTypedPermissionRequestPayload()
    {
        var ui = new FakeApprovalUi();
        ui.EnqueueVerdict("allow");
        var api = new PermissionsTestApi { HasUi = true, Ui = ui, SessionName = "s1" };
        var client = new ApprovalClient(api);

        await client.RequestApprovalAsync("bash", Args, "needs approval", "s1");

        var request = Assert.Single(ui.Requests);
        Assert.Equal(ApprovalClient.PermissionRequestKind, request.Kind);
        Assert.Equal(api.Descriptor.Id, request.ExtensionId);

        var payload = request.Payload;
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("bash", payload.GetProperty("tool").GetString());
        Assert.Equal("needs approval", payload.GetProperty("reason").GetString());
        Assert.Equal("deny", payload.GetProperty("defaultAnswer").GetString());
        Assert.Equal("s1", payload.GetProperty("sessionKey").GetString());
        Assert.Equal("git push origin", payload.GetProperty("arguments").GetProperty("command").GetString());
    }
}
