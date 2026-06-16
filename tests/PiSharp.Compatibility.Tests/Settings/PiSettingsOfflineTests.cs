using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Tests.Settings;

public class PiSettingsOfflineTests
{
    [Fact]
    public void Document_ParsesOfflineTrue()
    {
        var doc = PiSettingsDocument.Parse("""{"offline": true}""");
        Assert.True(doc.Settings.Offline);
    }

    [Fact]
    public void Document_ParsesOfflineFalse()
    {
        var doc = PiSettingsDocument.Parse("""{"offline": false}""");
        Assert.False(doc.Settings.Offline);
    }

    [Fact]
    public void Document_OfflineAbsentIsNull()
    {
        var doc = PiSettingsDocument.Parse("{}");
        Assert.Null(doc.Settings.Offline);
    }

    [Fact]
    public void MergedDocument_ProjectLayerOverridesGlobal()
    {
        var global = PiSettingsDocument.Parse("""{"offline": false}""");
        var project = PiSettingsDocument.Parse("""{"offline": true}""");
        var merged = PiSettingsDocument.MergeMany(
        [
            new PiSettingsLayerDocument(PiSettingsLayer.GlobalLegacy, global),
            new PiSettingsLayerDocument(PiSettingsLayer.ProjectLegacy, project)
        ], out _);
        Assert.True(merged.Settings.Offline);
    }
}
