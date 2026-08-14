using System.Text.Json;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// Unit tests for the daemon-side theme authority (plan C8): merge semantics, active-theme
/// resolution and the <see cref="ThemeRegistry.ApplyToSessionsAsync"/> broadcast helper.
/// </summary>
public sealed class ThemeRegistryTests
{
    [Fact]
    public async Task MergeAsync_LoadsDocumentsFromPaths_KeyedByName()
    {
        using var dir = NewThemeDir(("Dark", """{ "name": "Dark", "tokens": { "background": "#1e1e1e" } }"""));
        var registry = new ThemeRegistry();

        var merged = await registry.MergeAsync([dir.Path], CancellationToken.None);

        Assert.Equal(1, merged);
        var document = Assert.Single(registry.Documents);
        Assert.Equal("Dark", document.Name);
    }

    [Fact]
    public async Task MergeAsync_ReplacesExistingDocumentWithSameName()
    {
        using var dir = NewThemeDir(("Dark", """{ "name": "Dark", "tokens": { "background": "#000000" } }"""));
        var registry = new ThemeRegistry();
        await registry.MergeAsync([dir.Path], CancellationToken.None);

        File.WriteAllText(Path.Combine(dir.Path, "dark.json"), """{ "name": "Dark", "tokens": { "background": "#ffffff" } }""");
        var merged = await registry.MergeAsync([dir.Path], CancellationToken.None);

        Assert.Equal(1, merged);
        var document = Assert.Single(registry.Documents);
        Assert.Equal("#ffffff", document.Tokens!["background"]);
    }

    [Fact]
    public async Task MergeAsync_EmptyPaths_ReturnsZero()
    {
        var registry = new ThemeRegistry();

        var merged = await registry.MergeAsync([], CancellationToken.None);

        Assert.Equal(0, merged);
        Assert.Empty(registry.Documents);
    }

    [Fact]
    public async Task TrySetActive_MatchesCaseInsensitively_AndExposesDocument()
    {
        using var dir = NewThemeDir(("Dark", """{ "name": "Dark", "tokens": { "background": "#1e1e1e" } }"""));
        var registry = new ThemeRegistry();
        await registry.MergeAsync([dir.Path], CancellationToken.None);

        var set = registry.TrySetActive("dark");

        Assert.True(set);
        Assert.Equal("Dark", registry.ActiveName);
        Assert.NotNull(registry.ActiveDocument);
        Assert.Equal("Dark", registry.ActiveDocument!.Name);
    }

    [Fact]
    public async Task TrySetActive_UnknownName_ReturnsFalseAndKeepsActive()
    {
        using var dir = NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ThemeRegistry();
        await registry.MergeAsync([dir.Path], CancellationToken.None);
        Assert.True(registry.TrySetActive("Dark"));

        var set = registry.TrySetActive("Missing");

        Assert.False(set);
        Assert.Equal("Dark", registry.ActiveName);
    }

    [Fact]
    public async Task MergeAsync_PreservesActiveName()
    {
        using var dir = NewThemeDir(("Dark", """{ "name": "Dark" }"""), ("Light", """{ "name": "Light" }"""));
        var registry = new ThemeRegistry();
        await registry.MergeAsync([dir.Path], CancellationToken.None);
        Assert.True(registry.TrySetActive("Dark"));

        await registry.MergeAsync([dir.Path], CancellationToken.None);

        Assert.Equal("Dark", registry.ActiveName);
        Assert.Equal(2, registry.Documents.Count);
    }

    [Fact]
    public async Task Changed_RaisedOnMergeAndSetActive()
    {
        using var dir = NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ThemeRegistry();
        var count = 0;
        registry.Changed += (_, _) => count++;

        await registry.MergeAsync([dir.Path], CancellationToken.None);
        registry.TrySetActive("Dark");
        registry.TrySetActive("Missing");

        Assert.Equal(2, count);
    }

    internal static TempThemeDir NewThemeDir(params (string FileName, string Json)[] themes)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (fileName, json) in themes)
        {
            var file = Path.Combine(root, fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".json");
            File.WriteAllText(file, json);
        }

        return new TempThemeDir(root);
    }

    internal sealed class TempThemeDir(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static JsonElement SerializeData(object? data)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(data, PiSharp.Server.Serialization.ServerJsonSerializer.Options));
        return document.RootElement.Clone();
    }
}
