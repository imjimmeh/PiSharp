using System.Text.Json;
using PiSharp.Agent.Resources.Theme;
using Xunit;

namespace PiSharp.Agent.Tests;

public sealed class TuiThemeDocumentLoadAllTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string WriteThemeFile(string dir, string name)
    {
        var path = Path.Combine(dir, $"{name}.json");
        var theme = new TuiThemeDocument(name, new Dictionary<string, string> { ["token"] = "val" });
        File.WriteAllText(path, JsonSerializer.Serialize(theme, JsonOptions));
        return path;
    }

    [Fact]
    public async Task LoadAllAsync_ReturnsAllThemes()
    {
        using var tempDir = new TempDir();
        var path1 = WriteThemeFile(tempDir.Path, "dark");
        var path2 = WriteThemeFile(tempDir.Path, "light");

        var documents = await TuiThemeDocument.LoadAllAsync([path1, path2]);

        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, d => d.Name == "dark");
        Assert.Contains(documents, d => d.Name == "light");
    }

    [Fact]
    public async Task LoadAllAsync_FromDirectory_EnumeratesAllJsonFiles()
    {
        using var tempDir = new TempDir();
        WriteThemeFile(tempDir.Path, "alpha");
        WriteThemeFile(tempDir.Path, "beta");

        var documents = await TuiThemeDocument.LoadAllAsync([tempDir.Path]);

        Assert.Equal(2, documents.Count);
    }

    [Fact]
    public async Task LoadAllAsync_EmptyPaths_ReturnsEmpty()
    {
        var documents = await TuiThemeDocument.LoadAllAsync([]);
        Assert.Empty(documents);
    }

    [Fact]
    public async Task LoadAllAsync_SkipsNonExistentPaths()
    {
        var documents = await TuiThemeDocument.LoadAllAsync(["/nonexistent/path"]);
        Assert.Empty(documents);
    }

    [Fact]
    public async Task LoadAllAsync_SkipsInvalidJsonFiles()
    {
        using var tempDir = new TempDir();
        var validPath = WriteThemeFile(tempDir.Path, "valid");
        var invalidPath = Path.Combine(tempDir.Path, "invalid.json");
        File.WriteAllText(invalidPath, "{ not valid json");

        var documents = await TuiThemeDocument.LoadAllAsync([validPath, invalidPath]);

        Assert.Single(documents);
        Assert.Equal("valid", documents[0].Name);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-test-" + Guid.NewGuid().ToString("N")[..8]);
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
