using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.DeclarativeTools.Tests;

public sealed class ToolFileParserTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pi-declarative-tools-tests", Guid.NewGuid().ToString("N"));

    public ToolFileParserTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ToolFile Md(string path) => new(path, DeclarativeToolKind.Markdown, ToolFileShape.File);
    private static ToolFile Json(string path) => new(path, DeclarativeToolKind.Json, ToolFileShape.File);
    private static ToolFile Script(string path) => new(path, DeclarativeToolKind.Script, ToolFileShape.File);
    private static ToolFile Index(string path) => new(path, DeclarativeToolKind.Script, ToolFileShape.Index);

    [Fact]
    public void MdFrontmatter_KebabKeys_Parse()
    {
        var path = Write("hello.md", """
            ---
            name: my-tool
            label: My Tool
            description: "Searches the codebase."
            parameters:
              query:
                type: string
                description: "search text"
              limit:
                type: integer
            required: [query]
            prompt-snippet: "Search the codebase for a query"
            prompt-guidelines:
              - "Return file paths only."
            execution-mode: parallel
            renderer: my-renderer
            override: builtin
            ---

            Use this tool to search source files.
            """);

        var parser = new ToolFileParser();
        var tool = parser.Parse(Md(path), out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(tool);
        Assert.Equal("my-tool", tool!.Name);
        Assert.Equal("My Tool", tool.Label);
        Assert.StartsWith("Searches the codebase.", tool.Description);
        Assert.EndsWith("Use this tool to search source files.", tool.Description);
        Assert.Equal("Search the codebase for a query", tool.PromptSnippet);
        Assert.Equal(["Return file paths only."], tool.PromptGuidelines);
        Assert.Equal(ToolExecutionMode.Parallel, tool.ExecutionMode);
        Assert.Equal("my-renderer", tool.RendererName);
        Assert.Equal(ExtensionOverridePolicy.OverrideBuiltIn, tool.Override);
        Assert.False(tool.IsScript);
        Assert.Null(tool.ScriptPath);

        using var schema = JsonDocument.Parse(tool.ParametersSchema.GetRawText());
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("integer", schema.RootElement.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("query", schema.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void MdFrontmatter_NameFallsBackToFileName()
    {
        var path = Write("hello.md", "---\ndescription: Greets the user.\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(diagnostic);
        Assert.Equal("hello", tool!.Name);
        Assert.Equal("hello", tool.Label);
    }

    [Fact]
    public void MdFrontmatter_MissingDescription_IsDiagnostic()
    {
        var path = Write("hello.md", "---\nname: hello\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("description", diagnostic);
    }

    [Fact]
    public void MdFile_WithoutFrontmatter_IsDiagnostic()
    {
        var path = Write("hello.md", "Just body text, no frontmatter.\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("description", diagnostic);
    }

    [Fact]
    public void MdFrontmatter_UnknownKey_IsDiagnostic()
    {
        var path = Write("hello.md", "---\ndescription: hello\nprompt-snppet: typo\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("promptSnppet", diagnostic);
    }

    [Fact]
    public void MdFrontmatter_InvalidName_IsDiagnostic()
    {
        var path = Write("hello.md", "---\nname: bad name!\ndescription: hello\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("Invalid tool name", diagnostic);
    }

    [Fact]
    public void MdFrontmatter_DuplicateKey_IsDiagnostic()
    {
        var path = Write("hello.md", "---\ndescription: one\ndescription: two\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("YAML", diagnostic);
    }

    [Fact]
    public void MdFrontmatter_UnclosedDelimiter_IsDiagnostic()
    {
        var path = Write("hello.md", "---\ndescription: hello\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("closing", diagnostic);
    }

    [Fact]
    public void JsonFile_CamelCaseKeys_Parse()
    {
        var path = Write("hello.json", """
            {
              "name": "my-tool",
              "label": "My Tool",
              "description": "JSON tool.",
              "parameters": { "query": { "type": "string", "description": "q" } },
              "required": ["query"],
              "promptSnippet": "snip",
              "executionMode": "sequential",
              "override": "extension"
            }
            """);

        var tool = new ToolFileParser().Parse(Json(path), out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(tool);
        Assert.Equal("my-tool", tool!.Name);
        Assert.Equal("My Tool", tool.Label);
        Assert.Equal("JSON tool.", tool.Description);
        Assert.Equal("snip", tool.PromptSnippet);
        Assert.Equal(ToolExecutionMode.Sequential, tool.ExecutionMode);
        Assert.Equal(ExtensionOverridePolicy.Override, tool.Override);

        using var schema = JsonDocument.Parse(tool.ParametersSchema.GetRawText());
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("q", schema.RootElement.GetProperty("properties").GetProperty("query").GetProperty("description").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void JsonFile_DuplicateKey_IsDiagnostic()
    {
        var path = Write("hello.json", """{ "description": "a", "description": "b" }""");
        var tool = new ToolFileParser().Parse(Json(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("Duplicate key", diagnostic);
    }

    [Fact]
    public void JsonFile_UnknownKey_IsDiagnostic()
    {
        var path = Write("hello.json", """{ "description": "a", "descritpion": "typo" }""");
        var tool = new ToolFileParser().Parse(Json(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("descritpion", diagnostic);
    }

    [Fact]
    public void JsonFile_InvalidJson_IsDiagnostic()
    {
        var path = Write("hello.json", "{ not json");
        var tool = new ToolFileParser().Parse(Json(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("Invalid JSON", diagnostic);
    }

    [Fact]
    public void JsonFile_MissingDescription_IsDiagnostic()
    {
        var path = Write("hello.json", """{ "name": "hello" }""");
        var tool = new ToolFileParser().Parse(Json(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("description", diagnostic);
    }

    [Fact]
    public void IndexForm_NameDerivesFromDirectory()
    {
        var path = Write("tools/hello/index.sh", "echo hi\n");
        var tool = new ToolFileParser().Parse(Index(path), out var diagnostic);
        Assert.Null(diagnostic);
        Assert.Equal("hello", tool!.Name);
        Assert.Equal(path, tool.ScriptPath);
        Assert.True(tool.IsScript);
    }

    [Fact]
    public void Script_WithoutFrontmatter_UsesDefaults()
    {
        var path = Write("tool.sh", "#!/usr/bin/env bash\necho \"$PISHARP_TOOL_ARGS\"\n");
        var tool = new ToolFileParser().Parse(Script(path), out var diagnostic);
        Assert.Null(diagnostic);
        Assert.Equal("tool", tool!.Name);
        Assert.Equal("Runs the tool script. Reads JSON arguments from stdin.", tool.Description);
        Assert.Equal(path, tool.ScriptPath);
        Assert.Null(tool.Timeout);
        Assert.False(tool.AllowNonZeroExit);
        using var schema = JsonDocument.Parse(tool.ParametersSchema.GetRawText());
        Assert.Equal(0, schema.RootElement.GetProperty("properties").EnumerateObject().Count());
    }

    [Fact]
    public void Script_WithFrontmatter_ParsesScriptKeys()
    {
        var path = Write("fetch.py", """
            ---
            description: Fetches a URL.
            parameters:
              url:
                type: string
            required: [url]
            timeout-seconds: 45
            allow-non-zero-exit: true
            ---
            import sys, json
            print(json.load(sys.stdin))
            """);

        var tool = new ToolFileParser().Parse(Script(path), out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(tool);
        Assert.Equal("Fetches a URL.", tool!.Description);
        Assert.Equal(TimeSpan.FromSeconds(45), tool.Timeout);
        Assert.True(tool.AllowNonZeroExit);
        using var schema = JsonDocument.Parse(tool.ParametersSchema.GetRawText());
        Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("url").GetProperty("type").GetString());
        Assert.Equal("url", schema.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void Script_InvalidTimeout_IsDiagnostic()
    {
        var path = Write("tool.sh", "---\ntimeout-seconds: -5\n---\necho hi\n");
        var tool = new ToolFileParser().Parse(Script(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("timeout-seconds", diagnostic);
    }

    [Fact]
    public void Script_UnknownKey_IsDiagnostic()
    {
        var path = Write("tool.sh", "---\ntimeout-seconts: 5\n---\necho hi\n");
        var tool = new ToolFileParser().Parse(Script(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("timeoutSeconts", diagnostic);
    }

    [Fact]
    public void Override_RejectIsDefault()
    {
        var path = Write("tool.sh", "echo hi\n");
        var tool = new ToolFileParser().Parse(Script(path), out _);
        Assert.Equal(ExtensionOverridePolicy.Reject, tool!.Override);
    }

    [Fact]
    public void ExecutionMode_Invalid_IsDiagnostic()
    {
        var path = Write("hello.md", "---\ndescription: hello\nexecution-mode: sideways\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("execution-mode", diagnostic);
    }

    [Fact]
    public void Override_Invalid_IsDiagnostic()
    {
        var path = Write("hello.md", "---\ndescription: hello\noverride: everything\n---\n");
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("override", diagnostic);
    }

    [Fact]
    public void Parameters_InvalidSchema_IsDiagnostic()
    {
        var path = Write("hello.md", """
            ---
            description: hello
            parameters:
              query:
                type: banana
            ---
            """);
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("banana", diagnostic);
    }

    [Fact]
    public void Parameters_MissingType_IsDiagnostic()
    {
        var path = Write("hello.md", """
            ---
            description: hello
            parameters:
              query:
                description: no type here
            ---
            """);
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(tool);
        Assert.Contains("missing a 'type'", diagnostic);
    }

    [Fact]
    public void Parameters_AdditionalPropertiesTrue_IsHonored()
    {
        var path = Write("hello.md", """
            ---
            description: hello
            parameters:
              query:
                type: string
            additional-properties: true
            ---
            """);
        var tool = new ToolFileParser().Parse(Md(path), out var diagnostic);
        Assert.Null(diagnostic);
        Assert.NotNull(tool);
        using var schema = JsonDocument.Parse(tool!.ParametersSchema.GetRawText());
        Assert.True(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }
}
