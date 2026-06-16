using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.OpenAI;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class OpenAIProviderTests
{
    private static readonly AgentContext Context = new("system", [], []);
    private static readonly AgentStreamOptions Options = new(ApiKey: "key", MaxTokens: 50, Temperature: 0.2m);

    [Fact]
    public async Task ResponsesProviderMapsTextToolUsageResponseIdAndTerminalEvents()
    {
        var provider = Responses("data: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\",\"response_id\":\"resp_1\"}\n\n" +
                                 "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"call_id\":\"call 1\",\"name\":\"lookup\"}}\n\n" +
                                 "data: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{\\\"q\\\":1}\"}\n\n" +
                                 "data: {\"type\":\"response.output_item.done\"}\n\n" +
                                 "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}}\n\n");

        var events = await CollectAsync(provider, Model(OpenAIResponsesProvider.ApiName));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "hi");
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ToolCallEnd tool && tool.ToolCall.Name == "lookup");
        var done = Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Equal("resp_1", done.Message.ResponseId);
        Assert.Equal(2, done.Message.Usage!.Output);
    }

    [Fact]
    public async Task ResponsesProviderRequestIncludesExpectedFields()
    {
        var handler = new CapturingHandler("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        var provider = Responses(handler);

        await CollectAsync(provider, Model(OpenAIResponsesProvider.ApiName));

        Assert.Equal("/v1/responses", handler.Request!.RequestUri!.AbsolutePath);
        Assert.True(handler.Request.Headers.Contains("Authorization"));
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(50, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.2m, body.RootElement.GetProperty("temperature").GetDecimal());
    }

    [Fact]
    public async Task ResponsesProviderSerializesResponsesContentTypes()
    {
        var handler = new CapturingHandler("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        var provider = Responses(handler);
        using var arguments = JsonDocument.Parse("{\"action\":\"create\"}");
        var context = new AgentContext(
            "system",
            [
                new UserMessage([new TextContent("hello"), new ImageContent("image/png", "base64")]),
                new AssistantMessage([new TextContent("previous")]),
                new AssistantMessage([new ToolCallContent("call_fQ1Z0I68TIjMvqngIIwFFTDM", "todo", arguments.RootElement.Clone())]),
                AgentMessages.ToolResult("call_fQ1Z0I68TIjMvqngIIwFFTDM", "todo", "result")
            ],
            []);

        await CollectAsync(provider, Model(OpenAIResponsesProvider.ApiName) with { Input = ["text", "image"] }, context);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var input = body.RootElement.GetProperty("input");
        Assert.Equal("system", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("input_image", input[1].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("message", input[2].GetProperty("type").GetString());
        Assert.Equal("output_text", input[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("function_call", input[3].GetProperty("type").GetString());
        Assert.StartsWith("fc_", input[3].GetProperty("id").GetString());
        Assert.Equal("call_fQ1Z0I68TIjMvqngIIwFFTDM", input[3].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", input[4].GetProperty("type").GetString());
        Assert.Equal("call_fQ1Z0I68TIjMvqngIIwFFTDM", input[4].GetProperty("call_id").GetString());
    }

    [Fact]
    public async Task ResponsesProviderDoesNotDuplicateV1WhenBaseUrlIncludesVersion()
    {
        var handler = new CapturingHandler("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        var provider = Responses(handler);

        await CollectAsync(provider, Model(OpenAIResponsesProvider.ApiName) with { BaseUrl = "https://openai.test/v1" });

        Assert.Equal("/v1/responses", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CodexResponsesProviderUsesCodexEndpointAndSubscriptionHeaders()
    {
        var handler = new CapturingHandler("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        var token = FakeCodexJwt("account-123");
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler),
            new StaticCredentialResolver(new ProviderCredentialResult(BearerToken: token, IsAuthenticated: true)),
            "openai-codex-responses");
        var model = new ModelDescriptor("openai-codex", "gpt-5.5", "openai-codex-responses", BaseUrl: "https://chatgpt.com/backend-api", MaxTokens: 100);

        await CollectAsync(provider, model);

        Assert.Equal("/backend-api/codex/responses", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal($"Bearer {token}", handler.Request.Headers.GetValues("Authorization").Single());
        Assert.Equal("account-123", handler.Request.Headers.GetValues("chatgpt-account-id").Single());
        Assert.Equal("pi", handler.Request.Headers.GetValues("originator").Single());
        Assert.Equal("responses=experimental", handler.Request.Headers.GetValues("OpenAI-Beta").Single());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("system", body.RootElement.GetProperty("instructions").GetString());
        Assert.True(body.RootElement.GetProperty("store").GetBoolean() is false);
        Assert.Equal("low", body.RootElement.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("reasoning.encrypted_content", body.RootElement.GetProperty("include")[0].GetString());
        Assert.Equal("auto", body.RootElement.GetProperty("tool_choice").GetString());
        Assert.True(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.Empty(body.RootElement.GetProperty("input").EnumerateArray());
    }

    [Fact]
    public async Task CodexResponsesProviderSerializesUserContentAsInputText()
    {
        var handler = new CapturingHandler("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        var token = FakeCodexJwt("account-123");
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler),
            new StaticCredentialResolver(new ProviderCredentialResult(BearerToken: token, IsAuthenticated: true)),
            "openai-codex-responses");
        var model = new ModelDescriptor("openai-codex", "gpt-5.5", "openai-codex-responses", BaseUrl: "https://chatgpt.com/backend-api", MaxTokens: 100);
        var context = new AgentContext("codex instructions", [AgentMessages.User("hello")], []);

        await CollectAsync(provider, model, context);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("codex instructions", body.RootElement.GetProperty("instructions").GetString());
        var input = body.RootElement.GetProperty("input");
        Assert.Single(input.EnumerateArray());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatCompletionsProviderDoesNotDuplicateV1WhenBaseUrlIncludesVersion()
    {
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        var provider = new OpenAICompletionsProvider(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

        await CollectAsync(provider, Model(OpenAICompletionsProvider.ApiName) with { BaseUrl = "https://openai.test/v1" });

        Assert.Equal("/v1/chat/completions", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ChatCompletionsProviderMapsDeltaContentAndTerminalDone()
    {
        var provider = new OpenAICompletionsProvider(new HttpClient(new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                                                                                       "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

        var events = await CollectAsync(provider, Model(OpenAICompletionsProvider.ApiName));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "hello");
        Assert.IsType<AssistantMessageEvent.Done>(events.Last());
    }

    [Fact]
    public async Task ChatCompletionsProviderSerializesMessagesWithProviderJsonNames()
    {
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        var provider = new OpenAICompletionsProvider(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));
        var context = new AgentContext("system", [AgentMessages.User("hello")], []);

        await CollectAsync(provider, Model(OpenAICompletionsProvider.ApiName), context);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var firstMessage = body.RootElement.GetProperty("messages")[0];
        Assert.True(firstMessage.TryGetProperty("role", out _));
        Assert.True(firstMessage.TryGetProperty("content", out _));
        Assert.False(firstMessage.TryGetProperty("Role", out _));
        Assert.False(firstMessage.TryGetProperty("Content", out _));
        Assert.Equal("system", firstMessage.GetProperty("role").GetString());

        var userMessage = body.RootElement.GetProperty("messages")[1];
        Assert.Equal("user", userMessage.GetProperty("role").GetString());
        Assert.Equal("hello", userMessage.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ChatCompletionsProviderSerializesToolCallsAndResultsInOpenAIShape()
    {
        using var args = JsonDocument.Parse("{\"path\":\".\"}");
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        var provider = new OpenAICompletionsProvider(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));
        var context = new AgentContext(
            "system",
            [
                new UserMessage([new TextContent("list files")]),
                new AssistantMessage([new ToolCallContent("call_123", "bash", args.RootElement.Clone())]),
                new ToolResultMessage("call_123", "bash", [new TextContent("ok")], null, false)
            ],
            []);

        await CollectAsync(provider, Model(OpenAICompletionsProvider.ApiName), context);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");

        var assistantMessage = messages.EnumerateArray().First(message => message.GetProperty("role").GetString() == "assistant" && message.TryGetProperty("tool_calls", out _));
        Assert.True(assistantMessage.TryGetProperty("tool_calls", out var toolCalls));
        Assert.Equal("call_123", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("function", toolCalls[0].GetProperty("type").GetString());
        Assert.Equal("bash", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"path\":\".\"}", toolCalls[0].GetProperty("function").GetProperty("arguments").GetString());

        var toolMessage = messages.EnumerateArray().First(message => message.GetProperty("role").GetString() == "tool");
        Assert.Equal(JsonValueKind.String, toolMessage.GetProperty("content").ValueKind);
        Assert.Equal("ok", toolMessage.GetProperty("content").GetString());
        Assert.Equal("call_123", toolMessage.GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task ChatCompletionsProviderAssemblesToolCallDeltaArgumentsAndMapsStopReason()
    {
        var provider = new OpenAICompletionsProvider(new HttpClient(new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"q\\\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\":1}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n")), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

        var events = await CollectAsync(provider, Model(OpenAICompletionsProvider.ApiName));

        var tool = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events.First(evt => evt is AssistantMessageEvent.ToolCallEnd));
        Assert.Equal("lookup", tool.ToolCall.Name);
        Assert.Equal(1, tool.ToolCall.Arguments.GetProperty("q").GetInt32());
        var done = Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Equal("tool_use", done.Message.StopReason);
    }

    [Fact]
    public async Task NonSuccessResponsesEmitTerminalErrorAfterStart()
    {
        var provider = Responses(new CapturingHandler("bad", HttpStatusCode.BadRequest));

        var events = await CollectAsync(provider, Model(OpenAIResponsesProvider.ApiName));

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        Assert.IsType<AssistantMessageEvent.Error>(events.Last());
    }

    private static ModelDescriptor Model(string api) => new("openai", "gpt-test", api, BaseUrl: "https://openai.test", MaxTokens: 100);
    private static string FakeCodexJwt(string accountId)
    {
        var header = Base64Url("{\"alg\":\"none\"}");
        var payload = Base64Url($"{{\"https://api.openai.com/auth\":{{\"chatgpt_account_id\":\"{accountId}\"}}}}");
        return $"{header}.{payload}.signature";
    }

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static OpenAIResponsesProvider Responses(string sse) => Responses(new CapturingHandler(sse));
    private static OpenAIResponsesProvider Responses(CapturingHandler handler) => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer key" }, IsAuthenticated: true)));

    private static async Task<List<AssistantMessageEvent>> CollectAsync(PiSharp.Ai.Providers.IModelProvider provider, ModelDescriptor model, AgentContext? context = null)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in provider.StreamAsync(model, context ?? Context, Options)) events.Add(evt);
        return events;
    }
}
