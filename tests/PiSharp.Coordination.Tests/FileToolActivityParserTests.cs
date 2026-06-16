using System.Text.Json;
using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class FileToolActivityParserTests
{
    [Fact]
    public void ParserExtractsDirectFileToolActivity()
    {
        AssertRead("README.md", """{"filePath":"README.md"}""");
        AssertWrite("README.md", """{"filePath":"README.md"}""", toolName: "write");
        AssertWrite("README.md", """{"filePath":"README.md"}""", toolName: "edit");
    }

    [Fact]
    public void ParserRecognisesPathAndFileAliases()
    {
        AssertRead("src/foo.cs", """{"path":"src/foo.cs"}""");
        AssertWrite("src/bar.cs", """{"file":"src/bar.cs"}""", toolName: "write");
    }

    [Fact]
    public void ParserReturnsNullForBashAndUnrecognisedTools()
    {
        Assert.Null(FileToolActivityParser.Parse("bash", Json("""{"command":"cat README.md"}""")));
        Assert.Null(FileToolActivityParser.Parse("unknown", Json("""{"filePath":"x.txt"}""")));
    }

    [Fact]
    public void ParserReturnsNullWhenNoPathArgumentPresent()
    {
        Assert.Null(FileToolActivityParser.Parse("read", Json("""{"query":"something"}""")));
        Assert.Null(FileToolActivityParser.Parse("write", Json("""{"content":"hello"}""")));
        Assert.Null(FileToolActivityParser.Parse("edit", Json("""{}""")));
    }

    [Fact]
    public void ParserReturnsNullForNullEmptyWhitespacePathValue()
    {
        Assert.Null(FileToolActivityParser.Parse("read", Json("""{"filePath":""}""")));
        Assert.Null(FileToolActivityParser.Parse("write", Json("""{"filePath":"   "}""")));
        Assert.Null(FileToolActivityParser.Parse("read", Json("""{"path":null}""")));
    }

    [Fact]
    public void ParserHandlesApplyPatchAsWrite()
    {
        var result = FileToolActivityParser.Parse("apply_patch", Json("""{"filePath":"diff.patch"}"""));
        Assert.NotNull(result);
        Assert.Equal(FileActivityKind.Write, result!.Kind);
        Assert.Equal("diff.patch", result.FilePath);
    }

    [Fact]
    public void ParserPrefersFilePathOverPathOverFile()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"a.txt","path":"b.txt","file":"c.txt"}"""));
        Assert.NotNull(result);
        Assert.Equal("a.txt", result!.FilePath);

        result = FileToolActivityParser.Parse("read", Json("""{"path":"b.txt","file":"c.txt"}"""));
        Assert.NotNull(result);
        Assert.Equal("b.txt", result!.FilePath);
    }

    [Fact]
    public void ParserMatchesToolNamesCaseInsensitively()
    {
        Assert.NotNull(FileToolActivityParser.Parse("Read", Json("""{"filePath":"x.txt"}""")));
        Assert.NotNull(FileToolActivityParser.Parse("Write", Json("""{"filePath":"x.txt"}""")));
        Assert.NotNull(FileToolActivityParser.Parse("Edit", Json("""{"filePath":"x.txt"}""")));
    }

    [Fact]
    public void NormalizePathStripsLeadingDotSlash()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"./README.md"}"""));
        Assert.NotNull(result);
        Assert.Equal("README.md", result!.FilePath);

        result = FileToolActivityParser.Parse("read", Json("""{"path":"./src/foo.cs"}"""));
        Assert.NotNull(result);
        Assert.Equal("src/foo.cs", result!.FilePath);
    }

    [Fact]
    public void NormalizePathKeepsAbsolutePathsWhenNoRepoRoot()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"/tmp/out.txt"}"""));
        Assert.NotNull(result);
        Assert.Equal("/tmp/out.txt", result!.FilePath);
    }

    [Fact]
    public void NormalizePathConvertsBackslashToForwardSlash()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"src\\foo\\bar.cs"}"""));
        Assert.NotNull(result);
        Assert.Equal("src/foo/bar.cs", result!.FilePath);
    }

    [Fact]
    public void NormalizePathTrimsWhitespace()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"  readme.md  "}"""));
        Assert.NotNull(result);
        Assert.Equal("readme.md", result!.FilePath);
    }

    [Fact]
    public void ParseWithRepoRootNormalizesUnderRepoToRelative()
    {
        var repo = Path.GetFullPath("/home/user/project");

        var result = FileToolActivityParser.Parse("read",
            Json($$"""{"filePath":"{{EscapeJson(Path.Combine(repo, "src", "main.cs"))}}"}"""),
            repo);

        Assert.NotNull(result);
        Assert.Equal("src/main.cs", result!.FilePath);
    }

    [Fact]
    public void ParseWithRepoRootKeepsExternalPathUncollapsed()
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "my-repo"));

        var result = FileToolActivityParser.Parse("read",
            Json("""{"filePath":"/etc/config.ini"}"""),
            repo);

        Assert.NotNull(result);
        Assert.Equal("/etc/config.ini", result!.FilePath);
    }

    [Fact]
    public void ParseWithoutRepoRootKeepsRawBehavior()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"src/main.cs"}"""));
        Assert.NotNull(result);
        Assert.Equal("src/main.cs", result!.FilePath);
    }

    [Fact]
    public void NormalizePathDoesNotMangleUncPaths()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"\\\\server\\share\\file.txt"}"""));
        Assert.NotNull(result);
        Assert.Equal("//server/share/file.txt", result!.FilePath);
    }

    [Fact]
    public void NormalizePathCollapsesConsecutiveSlashes()
    {
        var result = FileToolActivityParser.Parse("read", Json("""{"filePath":"src//dir///file.cs"}"""));
        Assert.NotNull(result);
        Assert.Equal("src/dir/file.cs", result!.FilePath);
    }

    private static void AssertRead(string expectedPath, string argumentsJson, string toolName = "read")
    {
        var result = FileToolActivityParser.Parse(toolName, Json(argumentsJson));
        Assert.NotNull(result);
        Assert.Equal(FileActivityKind.Read, result!.Kind);
        Assert.Equal(expectedPath, result.FilePath);
    }

    private static void AssertWrite(string expectedPath, string argumentsJson, string toolName = "write")
    {
        var result = FileToolActivityParser.Parse(toolName, Json(argumentsJson));
        Assert.NotNull(result);
        Assert.Equal(FileActivityKind.Write, result!.Kind);
        Assert.Equal(expectedPath, result.FilePath);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string EscapeJson(string path) => path.Replace("\\", "\\\\");
}
