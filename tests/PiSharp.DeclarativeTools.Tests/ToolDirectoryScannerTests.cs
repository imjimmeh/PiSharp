using Xunit;

namespace PiSharp.DeclarativeTools.Tests;

public sealed class ToolDirectoryScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-declarative-tools-scanner", Guid.NewGuid().ToString("N"));
    private readonly ToolDirectoryScanner _scanner = new();

    public ToolDirectoryScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string ToolDir(params string[] segments)
    {
        var dir = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteFile(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Scan_FileForm_AcceptsAllKinds()
    {
        var dir = ToolDir("tools");
        WriteFile("tools/foo.md");
        WriteFile("tools/bar.json");
        WriteFile("tools/baz.sh");
        WriteFile("tools/qux.bash");
        WriteFile("tools/quux.py");

        var files = _scanner.Scan([dir]);

        Assert.Equal(5, files.Count);
        Assert.Contains(files, f => f.Path.EndsWith("foo.md") && f.Kind == DeclarativeToolKind.Markdown && f.Shape == ToolFileShape.File);
        Assert.Contains(files, f => f.Path.EndsWith("bar.json") && f.Kind == DeclarativeToolKind.Json);
        Assert.Contains(files, f => f.Path.EndsWith("baz.sh") && f.Kind == DeclarativeToolKind.Script);
        Assert.Contains(files, f => f.Path.EndsWith("qux.bash") && f.Kind == DeclarativeToolKind.Script);
        Assert.Contains(files, f => f.Path.EndsWith("quux.py") && f.Kind == DeclarativeToolKind.Script);
    }

    [Fact]
    public void Scan_IndexForm_OneLevelDown()
    {
        var dir = ToolDir("tools");
        WriteFile("tools/greet/index.sh");
        WriteFile("tools/calc/index.py");

        var files = _scanner.Scan([dir]);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Path.Replace('\\', '/').EndsWith("greet/index.sh") && f.Shape == ToolFileShape.Index);
        Assert.Contains(files, f => f.Path.Replace('\\', '/').EndsWith("calc/index.py") && f.Shape == ToolFileShape.Index);
    }

    [Fact]
    public void Scan_IgnoresDeeperNestingUnsupportedExtensionsAndHiddenNames()
    {
        var dir = ToolDir("tools");
        WriteFile("tools/a/b/c.sh");            // two levels deep → ignored
        WriteFile("tools/notes.txt");           // unsupported extension
        WriteFile("tools/.hidden.md");          // dot name
        WriteFile("tools/_private.json");       // underscore name
        WriteFile("tools/script.js");           // .js is never scanned
        WriteFile("tools/ok.md");

        var files = _scanner.Scan([dir]);

        var file = Assert.Single(files);
        Assert.EndsWith("ok.md", file.Path);
    }

    [Fact]
    public void Scan_IgnoresNonIndexFilesInSubdirectories()
    {
        var dir = ToolDir("tools");
        WriteFile("tools/foo/helper.sh");       // not index.* → ignored
        WriteFile("tools/foo/index.sh");

        var files = _scanner.Scan([dir]);

        var file = Assert.Single(files);
        Assert.EndsWith("index.sh", file.Path);
    }

    [Fact]
    public void Scan_MissingDirectory_IsSkippedSilently()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var files = _scanner.Scan([missing]);
        Assert.Empty(files);
    }

    [Fact]
    public void Scan_OrderIsDeterministicByPath()
    {
        var dir = ToolDir("tools");
        WriteFile("tools/z.md");
        WriteFile("tools/a.md");
        WriteFile("tools/m.md");

        var first = _scanner.Scan([dir]);
        var second = _scanner.Scan([dir]);

        Assert.Equal(first.Select(f => f.Path), second.Select(f => f.Path));
        Assert.Equal("a.md", Path.GetFileName(first[0].Path));
        Assert.Equal("m.md", Path.GetFileName(first[1].Path));
        Assert.Equal("z.md", Path.GetFileName(first[2].Path));
    }

    [Fact]
    public void ResolveToolDirectories_ConfiguredEntries_AreResolvedAgainstCwd()
    {
        var dirs = _scanner.ResolveToolDirectories([".pi/PiSharp/tools", "C:/absolute/tools"], "C:/project");

        Assert.Equal(2, dirs.Count);
        Assert.Equal(Path.GetFullPath(".pi/PiSharp/tools", "C:/project").Replace('\\', '/'), dirs[0].Replace('\\', '/'));
        Assert.Equal("C:/absolute/tools", dirs[1].Replace('\\', '/'));
    }

    [Fact]
    public void ResolveToolDirectories_EmptyConfigured_UsesDefaults()
    {
        var dirs = _scanner.ResolveToolDirectories([], "C:/project");
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(".pi", "PiSharp", "tools"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine("C:/project", ".pi", "PiSharp", "tools"), StringComparison.OrdinalIgnoreCase));
    }
}
