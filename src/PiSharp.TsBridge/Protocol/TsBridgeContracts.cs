using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Extensions;

namespace PiSharp.TsBridge.Protocol;

public sealed record TsResourceListItem(string Kind, string Path, string? Source = null, string? Package = null);
public sealed record TsResourceReadResult(string Path, string? Content = null, string? Error = null);

public sealed record JsonRpcRequest(string Jsonrpc, string Method, object? Params = null, string? Id = null);
public sealed record JsonRpcResponse(string Jsonrpc, string? Id, object? Result = null, JsonRpcError? Error = null);
public sealed record JsonRpcError(int Code, string Message, object? Data = null);

public sealed record TsExtensionLoadTimings(
    double CacheLookup = 0,
    double CompilerLoad = 0,
    double Transpile = 0,
    double DependencyTranspile = 0,
    double ModuleImport = 0,
    double Activation = 0,
    double RegistrationFlush = 0,
    double Total = 0,
    int CacheHits = 0,
    int CacheMisses = 0,
    int CacheFallbacks = 0);

public sealed record TsExtensionLoadResult(
    bool Ok,
    string? ExtensionPath = null,
    string? Error = null,
    bool Skipped = false,
    TsExtensionLoadTimings? Timings = null,
    TsExtensionDescriptor? Descriptor = null);

public sealed record TsExtensionDescriptor(
    int SchemaVersion,
    string ExtensionPath,
    string? SourceHash = null,
    IReadOnlyList<TsDescriptorDependency>? DependencyHashes = null,
    IReadOnlyList<TsToolDefinition>? Tools = null,
    IReadOnlyList<TsCommandRegistration>? Commands = null,
    IReadOnlyList<TsShortcutRegistration>? Shortcuts = null,
    IReadOnlyList<TsFlagRegistration>? Flags = null,
    IReadOnlyList<TsPromptSectionRegistration>? PromptSections = null,
    IReadOnlyList<TsPromptTransformRegistration>? PromptTransforms = null,
    IReadOnlyList<TsProviderRegistration>? Providers = null,
    IReadOnlyList<TsSkillRegistration>? Skills = null,
    IReadOnlyList<string>? ProvidesServices = null,
    IReadOnlyList<string>? ConsumesServices = null,
    string Activation = "auto",
    string? PackageName = null,
    string? PackageVersion = null);

public sealed record TsDescriptorDependency(string Path, string Hash);

public sealed record TsExtensionInitializeResult(bool Ok = true, IReadOnlyList<TsExtensionLoadResult>? Results = null);
public sealed record TsExtensionsLoadRequest(IReadOnlyList<string> ExtensionPaths, int? Concurrency = null, bool HasUi = false, string? SessionId = null, IReadOnlyList<ExtensionCommandInfo>? Commands = null, object? Session = null);
public sealed record TsExtensionsLoadResult(bool Ok = true, IReadOnlyList<TsExtensionLoadResult>? Results = null);
public sealed record TsExtensionBackgroundLoadStatus(string? ExtensionPath = null, bool Complete = false, TsExtensionLoadResult? Result = null, string? Error = null);
public sealed record TsExtensionBackgroundLoadStatuses(IReadOnlyList<TsExtensionBackgroundLoadStatus>? Statuses = null);

public sealed record TsRuntimeActionRequest(string ExtensionId, string Action, object? Payload = null);
public sealed record TsRuntimeActionResult(object? Value = null, bool Ok = true, string? Error = null);
public sealed record TsCommandInvokeRequest(string ExtensionId, string Name, string Args);
public sealed record TsCommandInvokeResult(bool Handled = true, string? Message = null, bool IsError = false);
public sealed record TsToolDefinition
{
    [JsonConstructor]
    public TsToolDefinition(
        string extensionId,
        string name,
        string label,
        string description,
        JsonElement parameters,
        ToolExecutionMode? executionMode = null,
        string? promptSnippet = null,
        IReadOnlyList<string>? promptGuidelines = null,
        string? renderShell = null,
        string? rendererName = null,
        bool hasRenderCall = false,
        bool hasRenderResult = false)
    {
        ExtensionId = extensionId;
        Name = name;
        Label = label;
        Description = description;
        Parameters = parameters;
        ExecutionMode = executionMode;
        PromptSnippet = promptSnippet;
        PromptGuidelines = promptGuidelines;
        RenderShell = renderShell;
        RendererName = rendererName;
        HasRenderCall = hasRenderCall;
        HasRenderResult = hasRenderResult;
    }

    public TsToolDefinition(string name, string label, string description, JsonElement parameters, ToolExecutionMode? executionMode = null)
        : this("default", name, label, description, parameters, executionMode) { }

    public string ExtensionId { get; init; }
    public string Name { get; init; }
    public string Label { get; init; }
    public string Description { get; init; }
    public JsonElement Parameters { get; init; }
    public ToolExecutionMode? ExecutionMode { get; init; }
    public string? PromptSnippet { get; init; }
    public IReadOnlyList<string>? PromptGuidelines { get; init; }
    public string? RenderShell { get; init; }
    public string? RendererName { get; init; }
    public bool HasRenderCall { get; init; }
    public bool HasRenderResult { get; init; }
}
public sealed record TsToolExecuteRequest(string ToolCallId, string Name, JsonElement Parameters);
public sealed record TsToolExecuteResult(IReadOnlyList<MessageContent> Content, object? Details = null, bool Terminate = false, bool IsError = false);
public sealed record TsToolCallRequest(string ExtensionId, string Name, JsonElement Arguments, string? ToolCallId = null);
public sealed record TsToolCallResult(IReadOnlyList<MessageContent> Content, object? Details = null, bool Terminate = false, bool IsError = false);
public sealed record TsToolRenderRequest(string ExtensionId, string Name, string ToolCallId, JsonElement? Arguments, TsToolCallResult? Result, bool IsPartial = false, bool IsError = false, bool Expanded = false, int Width = 120);
public sealed record TsToolRenderResult(IReadOnlyList<string> Lines);
public sealed record TsEventForward(string Name, object? Payload);
public sealed record TsEventDispatchResult(
    string? SystemPrompt = null,
    IReadOnlyList<AgentMessage>? Messages = null,
    PromptDocumentPatch? Patch = null,
    string? Action = null,
    string? Text = null,
    IReadOnlyList<ImageContent>? Images = null,
    bool? Cancel = null,
    string? Reason = null,
    string[]? SkillPaths = null,
    string[]? PromptPaths = null,
    string[]? ThemePaths = null,
    ExtensionBashOperations? Operations = null,
    ExtensionBashResult? BashResult = null);
public sealed record TsProviderConfig(string Name, string Api, string? BaseUrl = null, string? ApiKey = null, IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyList<TsProviderModel>? Models = null, bool HasOAuth = false, bool HasCustomStreamHandler = false);
public sealed record TsProviderRegistration(string ExtensionId, string Name, string Api, string? BaseUrl = null, string? ApiKey = null, IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyList<TsProviderModel>? Models = null, bool HasOAuth = false, bool HasCustomStreamHandler = false)
{
    public TsProviderConfig ToConfig() => new(Name, Api, BaseUrl, ApiKey, Headers, Models, HasOAuth, HasCustomStreamHandler);
}
public sealed record TsProviderModel(string Provider, string Id, string? Name = null, int ContextWindow = 0, int MaxTokens = 0, bool Reasoning = false);
public sealed record TsSkillRegistration(
    string ExtensionId,
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false,
    string Override = "reject");
public sealed record TsExtensionRegistration(string ExtensionId, string SourceId, string Kind, string Name, object Payload);
public sealed record TsCommandRegistration(string ExtensionId, string Name, string Description);
public sealed record TsShortcutRegistration(string ExtensionId, string Keys, string Description);
public sealed record TsFlagRegistration(string ExtensionId, string Name, string Description, string Type = "boolean", object? DefaultValue = null);
public sealed record TsUiRequest(string RequestId, string ExtensionId, string Kind, string Title, string? Message, IReadOnlyList<string>? Options, object? Component, JsonElement Payload = default);
public sealed record TsUiResponse(string RequestId, object? Value, bool Cancelled);
public sealed record TsProviderCallbackRequest(string ProviderApi, string Method, object Payload);
public sealed record TsProviderUnregisterRequest(string ExtensionId, string Name);
public sealed record TsCustomUiSnapshot(
    string RequestId,
    IReadOnlyList<string> Lines,
    int Width,
    int Height,
    bool Completed = false,
    object? Value = null,
    string? Error = null);

public sealed record TsCustomUiInputRequest(
    string RequestId,
    string? Data = null,
    int? Width = null,
    int? Height = null,
    string? Event = null);

public sealed record TsPromptSectionRegistration(
    string ExtensionId,
    string Id,
    string Slot,
    int Priority,
    string Content,
    string? Title = null,
    bool Protected = false,
    string Override = "reject");
public sealed record TsPromptTransformRegistration(
    string ExtensionId,
    string Name,
    string Mode = "append",
    string? AppendMarkdown = null,
    IReadOnlyList<string>? RemoveSectionIds = null);

public sealed record TsMessageRendererRegistration(
    string ExtensionId,
    string Name,
    string? RowType = null,
    string? CustomType = null,
    string Override = "reject");

public sealed record TsMessageDecoratorRegistration(
    string ExtensionId,
    string Name,
    string? RowType = null,
    string? CustomType = null,
    int Order = 0);

public sealed record TsMessageRenderRequest(
    string ExtensionId,
    string Name,
    string CustomType,
    object? Content,
    bool Display,
    object? Details,
    bool Expanded,
    int Width,
    string Role,
    string Text,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record TsMessageRenderResponse(
    string[] Lines,
    bool? PreserveBuiltIn = null);

public sealed record TsMessageDecorateRequest(
    string ExtensionId,
    string Name,
    string CustomType,
    string Text,
    string Role,
    TsMessageRenderRow[] Rows,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record TsMessageDecorateResponse(
    TsMessageRenderRow[] Rows);

public sealed record TsMessageRenderRow(
    string Text,
    string Kind = "normal",
    IReadOnlyList<TsMessageRenderSpan>? Spans = null);

public sealed record TsMessageRenderSpan(
    string Text,
    string Kind = "text");
