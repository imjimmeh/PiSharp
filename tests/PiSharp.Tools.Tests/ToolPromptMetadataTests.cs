using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class ToolPromptMetadataTests
{
    [Fact]
    public void BuiltInToolsExposeJavascriptPromptSnippets()
    {
        var tools = BuiltInTools.CreateAll(new FakeExecutionEnv("/repo"));

        Assert.Equal("Read file contents", tools["read"].PromptSnippet);
        Assert.Equal("Execute bash commands (ls, grep, find, etc.)", tools["bash"].PromptSnippet);
        Assert.Equal("Make precise file edits with exact text replacement", tools["edit"].PromptSnippet);
        Assert.Equal("Create or overwrite files", tools["write"].PromptSnippet);
        Assert.Equal("Search file contents", tools["grep"].PromptSnippet);
        Assert.Equal("Find files and directories", tools["find"].PromptSnippet);
        Assert.Equal("List directory contents", tools["ls"].PromptSnippet);
    }

    [Fact]
    public void BuiltInToolsExposePromptGuidelines()
    {
        var tools = BuiltInTools.CreateAll(new FakeExecutionEnv("/repo"));

        Assert.Contains("Use read to examine files instead of cat or sed.", tools["read"].PromptGuidelines);
        Assert.Contains("Use write only for new files or complete rewrites.", tools["write"].PromptGuidelines);
        Assert.Contains("Use edit for precise changes (edits[].oldText must match exactly)", tools["edit"].PromptGuidelines);
    }
}
