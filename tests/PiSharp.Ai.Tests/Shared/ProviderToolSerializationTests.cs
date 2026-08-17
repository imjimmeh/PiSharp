using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Providers.Anthropic;
using PiSharp.Ai.Providers.Bedrock;
using PiSharp.Ai.Providers.Google;
using PiSharp.Ai.Providers.Mistral;
using PiSharp.Ai.Providers.OpenAI;
using PiSharp.Tools;
using Xunit;

namespace PiSharp.Ai.Tests.Shared;

public sealed class ProviderToolSerializationTests
{
    private static readonly ModelDescriptor Model = new("provider", "model", "api", MaxTokens: 100, Input: ["text"]);
    private static readonly AgentStreamOptions Options = new(MaxTokens: 32);

    [Fact]
    public void ProviderPayloadsIncludeToolsFromAgentContext()
    {
        var context = new AgentContext("system", Array.Empty<AgentMessage>(), [new FakeTool()]);
        Assert.Equal("demo", PayloadFor(typeof(AnthropicProvider), context).GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("function", PayloadFor(typeof(OpenAIResponsesProvider), context).GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("demo", PayloadFor(typeof(OpenAICompletionsProvider), context).GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("demo", PayloadFor(typeof(GoogleProvider), context).GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Equal("demo", PayloadFor(typeof(BedrockProvider), context).GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
        Assert.Equal("demo", PayloadFor(typeof(MistralProvider), context).GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void ProviderPayloadsOmitToolsWhenContextHasNone()
    {
        var context = new AgentContext("system", Array.Empty<AgentMessage>());
        Assert.False(PayloadFor(typeof(AnthropicProvider), context).TryGetProperty("tools", out _));
        Assert.False(PayloadFor(typeof(OpenAIResponsesProvider), context).TryGetProperty("tools", out _));
    }

    [Fact]
    public void ProviderPayloadsPreserveGeneratedToolSchemas()
    {
        var context = new AgentContext("system", Array.Empty<AgentMessage>(), [new FakeTool(ToolSchemas.FromType<GeneratedBashLikeInput>())]);

        AssertGeneratedBashLikeSchema(PayloadFor(typeof(AnthropicProvider), context).GetProperty("tools")[0].GetProperty("input_schema"));
        AssertGeneratedBashLikeSchema(PayloadFor(typeof(OpenAIResponsesProvider), context).GetProperty("tools")[0].GetProperty("parameters"));
        AssertGeneratedBashLikeSchema(PayloadFor(typeof(OpenAICompletionsProvider), context).GetProperty("tools")[0].GetProperty("function").GetProperty("parameters"));
        AssertGeneratedBashLikeSchema(PayloadFor(typeof(GoogleProvider), context).GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parameters"));
        AssertGeneratedBashLikeSchema(PayloadFor(typeof(BedrockProvider), context).GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("inputSchema").GetProperty("json"));
        AssertGeneratedBashLikeSchema(PayloadFor(typeof(MistralProvider), context).GetProperty("tools")[0].GetProperty("function").GetProperty("parameters"));
    }

    [Fact]
    public void ProviderPayloadsSanitizeBooleanSchemasFromExternalTools()
    {
        var rawSchema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "request": true,
                    "params": true,
                    "nested": {
                        "type": "object",
                        "properties": {
                            "inner": true
                        }
                    }
                }
            }
            """).RootElement;

        var context = new AgentContext("system", Array.Empty<AgentMessage>(), [new FakeTool(rawSchema)]);
        var anthropicTools = PayloadFor(typeof(AnthropicProvider), context).GetProperty("tools");
        var inputSchema = anthropicTools[0].GetProperty("input_schema");

        Assert.Equal("object", inputSchema.GetProperty("type").GetString());
        var properties = inputSchema.GetProperty("properties");
        Assert.Equal(JsonValueKind.Object, properties.GetProperty("request").ValueKind);
        Assert.Equal("object", properties.GetProperty("request").GetProperty("type").GetString());

        Assert.Equal(JsonValueKind.Object, properties.GetProperty("params").ValueKind);
        Assert.Equal("object", properties.GetProperty("params").GetProperty("type").GetString());

        var nested = properties.GetProperty("nested");
        Assert.Equal(JsonValueKind.Object, nested.GetProperty("properties").GetProperty("inner").ValueKind);
        Assert.Equal("object", nested.GetProperty("properties").GetProperty("inner").GetProperty("type").GetString());
    }

    private static void AssertGeneratedBashLikeSchema(JsonElement schema)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("command").GetProperty("type").GetString());
        Assert.Equal("Bash command to execute", properties.GetProperty("command").GetProperty("description").GetString());
        Assert.Equal("Timeout in seconds (optional, no default timeout)", properties.GetProperty("timeout").GetProperty("description").GetString());
        Assert.Equal(["number", "null"], properties.GetProperty("timeout").GetProperty("type").EnumerateArray().Select(type => type.GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("timeout").GetProperty("default").ValueKind);
        Assert.Contains("command", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!));
    }

    private static JsonElement PayloadFor(Type providerType, AgentContext context)
    {
        var method = providerType.GetMethod("BuildPayload", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
            ?? throw new InvalidOperationException($"Missing BuildPayload on {providerType.Name}");
        return (JsonElement)method.Invoke(null, [Model, context, Options])!;
    }

    private sealed class FakeTool : IAgentTool
    {
        public FakeTool(JsonElement? parametersSchema = null)
        {
            ParametersSchema = parametersSchema?.Clone()
                ?? JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}").RootElement.Clone();
        }

        public string Name => "demo";
        public string Label => "demo";
        public string Description => "Demo tool";
        public JsonElement ParametersSchema { get; }
        public ToolExecutionMode? ExecutionMode => null;
        public JsonElement PrepareArguments(JsonElement args) => args;
        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
            => Task.FromResult(new AgentToolResult<object?>(Array.Empty<MessageContent>(), null));
    }

    private sealed record GeneratedBashLikeInput(
        [property: Description("Bash command to execute")]
        string Command,

        [property: Description("Timeout in seconds (optional, no default timeout)")]
        double? Timeout = null);
}
