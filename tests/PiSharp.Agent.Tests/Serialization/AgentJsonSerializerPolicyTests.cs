using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Serialization;
using Xunit;

namespace PiSharp.Agent.Tests.Serialization;

public sealed class AgentJsonSerializerPolicyTests
{
    [Fact]
    public void SerializesCamelCaseCompactJsonAndOmitsNulls()
    {
        var json = AgentJsonSerializer.Serialize<AgentMessage>(new AssistantMessage([new TextContent("hello")], Provider: "test-provider"));

        Assert.Contains("\"provider\":\"test-provider\"", json);
        Assert.DoesNotContain("Provider", json);
        Assert.DoesNotContain(": ", json);
        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("errorMessage", json);
    }
}
