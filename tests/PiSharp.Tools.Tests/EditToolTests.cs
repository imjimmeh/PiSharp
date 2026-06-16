using PiSharp.Tools.Edit;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class EditToolTests
{
    [Fact]
    public async Task EditToolAppliesMultipleDisjointEdits()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("a.txt", "one\ntwo\nthree");
        var tool = new EditTool(env);
        await tool.ExecuteAsync("tc1", new EditToolInput("a.txt", [new EditReplacement("one", "1"), new EditReplacement("three", "3")]));
        Assert.Equal("1\ntwo\n3", env.ReadFileOrDefault("a.txt"));
    }

    [Fact]
    public async Task EditToolRejectsOverlappingEdits()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("a.txt", "abcdef");
        var tool = new EditTool(env);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("tc1", new EditToolInput("a.txt", [new EditReplacement("abc", "x"), new EditReplacement("bc", "y")])));
        Assert.Contains("overlap", error.Message);
    }

    [Fact]
    public async Task EditToolPreservesCrLfLineEndings()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("a.txt", "one\r\ntwo\r\n");
        var tool = new EditTool(env);
        await tool.ExecuteAsync("tc1", new EditToolInput("a.txt", [new EditReplacement("two", "2")]));
        Assert.Equal("one\r\n2\r\n", env.ReadFileOrDefault("a.txt"));
    }

    [Fact]
    public void EditToolSchemaUsesTypescriptFieldNames()
    {
        var json = new EditTool(new FakeExecutionEnv()).ParametersSchema.GetRawText();
        Assert.Contains("path", json);
        Assert.Contains("edits", json);
        Assert.Contains("oldText", json);
        Assert.Contains("newText", json);
        Assert.DoesNotContain("filePath", json);
        Assert.DoesNotContain("oldString", json);
        Assert.DoesNotContain("newString", json);
        Assert.DoesNotContain("insertLine", json);
        Assert.DoesNotContain("dryRun", json);
    }
}
