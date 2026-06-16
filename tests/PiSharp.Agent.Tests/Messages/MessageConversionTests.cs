using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Messages;
using Xunit;

namespace PiSharp.Agent.Tests.Messages;

public sealed class MessageConversionTests
{
    [Fact]
    public void ConvertToLlmPassesThroughProviderMessages()
    {
        var user = AgentMessages.User("hello");
        var converted = MessageConverter.ConvertToLlm([user]);
        Assert.Same(user, converted[0]);
    }

    [Fact]
    public void ConvertToLlmFormatsBashExecutionAsUserText()
    {
        var converted = MessageConverter.ConvertToLlm([
            new BashExecutionMessage("dotnet test", "ok", 0, false, false)
        ]);
        var user = Assert.IsType<UserMessage>(converted[0]);
        var text = Assert.IsType<TextContent>(user.Content[0]);
        Assert.Contains("Ran `dotnet test`", text.Text);
        Assert.Contains("ok", text.Text);
    }

    [Fact]
    public void ConvertToLlmDropsExcludedBashExecution()
    {
        var converted = MessageConverter.ConvertToLlm([
            new BashExecutionMessage("secret", "output", 0, false, false, ExcludeFromContext: true)
        ]);
        Assert.Empty(converted);
    }

    [Fact]
    public void ConvertToLlmWrapsBranchAndCompactionSummaries()
    {
        var converted = MessageConverter.ConvertToLlm([
            new BranchSummaryMessage("branch work", "abc"),
            new CompactionSummaryMessage("old work", 100)
        ]);
        Assert.Contains("<summary>", ((TextContent)((UserMessage)converted[0]).Content[0]).Text);
        Assert.Contains("old work", ((TextContent)((UserMessage)converted[1]).Content[0]).Text);
    }
}
