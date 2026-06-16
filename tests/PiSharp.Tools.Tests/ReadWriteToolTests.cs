using PiSharp.Abstractions.Messages;
using PiSharp.Tools.Files;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class ReadWriteToolTests
{
    [Fact]
    public async Task WriteToolCreatesParentDirectoriesAndWritesContent()
    {
        var env = new FakeExecutionEnv();
        var tool = new WriteTool(env);
        await tool.ExecuteAsync("tc1", new WriteToolInput("src/new.txt", "hello"));
        Assert.Equal("hello", env.ReadFileOrDefault("/repo/src/new.txt"));
    }

    [Fact]
    public async Task ReadToolHonorsOffsetAndLimit()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("notes.txt", "one\ntwo\nthree\nfour");
        var tool = new ReadTool(env);
        var result = await tool.ExecuteAsync("tc1", new ReadToolInput("notes.txt", Offset: 2, Limit: 2));
        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.StartsWith("two\nthree", text);
        Assert.Contains("Use offset=4", text);
    }

    [Fact]
    public void ReadAndWriteSchemasUseTypescriptFieldNames()
    {
        var env = new FakeExecutionEnv();
        Assert.Contains("path", new ReadTool(env).ParametersSchema.GetRawText());
        Assert.Contains("content", new WriteTool(env).ParametersSchema.GetRawText());
    }
}
