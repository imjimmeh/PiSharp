using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Resources;
using PiSharp.Extensions;

namespace PiSharp.Agent.Harness;

public sealed record AgentHarnessOptions<TMetadata>(
    ISession<TMetadata> Session,
    ModelDescriptor Model,
    AgentStreamAsync StreamAsync,
    AgentCompletionAsync CompletionAsync,
    IReadOnlyList<IAgentTool> Tools,
    IReadOnlyList<string>? ActiveToolNames = null,
    SystemPromptBuildOptions? SystemPrompt = null,
    SystemPromptCompositionContext? SystemPromptContext = null,
    ISystemPromptComposer? SystemPromptComposer = null,
    Func<ISystemPromptComposer>? SystemPromptComposerFactory = null,
    Skill[]? Skills = null,
    PromptTemplate[]? PromptTemplates = null,
    ThinkingLevel ThinkingLevel = ThinkingLevel.Off,
    ToolExecutionMode ToolExecution = ToolExecutionMode.Parallel,
    AgentStreamOptions? StreamOptions = null,
    ExtensionRegistry? Extensions = null) where TMetadata : ISessionMetadata;
