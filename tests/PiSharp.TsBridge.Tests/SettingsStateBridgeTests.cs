using PiSharp.Extensions;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

/// <summary>
/// Verifies the P02 settings/state bridge surface: the 13 new runtime actions, the
/// <c>pi.settings.*</c>/<c>pi.state.*</c> API-surface members, and the <c>settings_changed</c>
/// event are all declared in the default manifest.
/// </summary>
public sealed class SettingsStateBridgeTests
{
    private static TsBridgeManifest Manifest => TsBridgeManifestFactory.CreateDefault();

    [Theory]
    [InlineData(nameof(TsBridgeRuntimeActions.SettingsGet), "settings_get")]
    [InlineData(nameof(TsBridgeRuntimeActions.SettingsGetCore), "settings_get_core")]
    [InlineData(nameof(TsBridgeRuntimeActions.SettingsSet), "settings_set")]
    [InlineData(nameof(TsBridgeRuntimeActions.SettingsRemove), "settings_remove")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateGet), "state_get")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateSet), "state_set")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateRemove), "state_remove")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateGetAll), "state_get_all")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateListKeys), "state_list_keys")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateClear), "state_clear")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateGetSchemaVersion), "state_get_schema_version")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateSetSchemaVersion), "state_set_schema_version")]
    [InlineData(nameof(TsBridgeRuntimeActions.StateRegisterMigration), "state_register_migration")]
    public void ManifestDeclaresSettingsStateRuntimeActions(string name, string wire)
        => Assert.Equal(wire, Manifest.Protocol.RuntimeActions[name]);

    [Theory]
    [InlineData("pi.settings", "get", "SettingsGet")]
    [InlineData("pi.settings", "getCore", "SettingsGetCore")]
    [InlineData("pi.settings", "set", "SettingsSet")]
    [InlineData("pi.settings", "remove", "SettingsRemove")]
    [InlineData("pi.state", "get", "StateGet")]
    [InlineData("pi.state", "set", "StateSet")]
    [InlineData("pi.state", "remove", "StateRemove")]
    [InlineData("pi.state", "getAll", "StateGetAll")]
    [InlineData("pi.state", "listKeys", "StateListKeys")]
    [InlineData("pi.state", "clear", "StateClear")]
    [InlineData("pi.state", "getSchemaVersion", "StateGetSchemaVersion")]
    [InlineData("pi.state", "setSchemaVersion", "StateSetSchemaVersion")]
    public void ManifestExposesPiSettingsAndPiStateSurfaces(string surface, string name, string action)
    {
        var member = Assert.Single(Manifest.ApiSurface.Members, m => m.Surface == surface && m.Name == name);
        Assert.Equal(TsBridgeApiMemberStatuses.RuntimeAction, member.Status);
        Assert.Equal(Manifest.Protocol.RuntimeActions[action], member.RuntimeAction);
    }

    [Fact]
    public void PiSettingsOnChangeIsImplementedSugar()
    {
        var member = Assert.Single(Manifest.ApiSurface.Members, m => m.Surface == "pi.settings" && m.Name == "onChange");
        Assert.Equal(TsBridgeApiMemberStatuses.Implemented, member.Status);
    }

    [Fact]
    public void StateRegisterMigrationIsNotExposedOnTheTsSurface()
        => Assert.DoesNotContain(Manifest.ApiSurface.Members, m => m.Name == "registerMigration");

    [Fact]
    public void ManifestDeclaresSettingsChangedEvent()
        => Assert.Contains(Manifest.ApiSurface.Events, evt => evt.Name == ExtensionEventNames.SettingsChanged && evt.Status == TsBridgeApiMemberStatuses.Implemented);
}
