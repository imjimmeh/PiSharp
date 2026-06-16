using PiSharp.TsBridge.Protocol;
using PiSharp.TsBridge.Shims;

namespace PiSharp.TsBridge;

public static class TsBridgeMethods
{
    public const string RegisterTool = "register_tool";
    public const string RegisterSkill = "register_skill";
    public const string RegisterProvider = "register_provider";
    public const string UnregisterProvider = "unregister_provider";
    public const string RegisterCommand = "register_command";
    public const string RegisterShortcut = "register_shortcut";
    public const string RegisterFlag = "register_flag";
    public const string RegisterPromptSection = "register_prompt_section";
    public const string RegisterPromptTransform = "register_prompt_transform";
    public const string RegisterMessageRenderer = "register_message_renderer";
    public const string UnregisterMessageRenderer = "unregister_message_renderer";
    public const string RegisterMessageDecorator = "register_message_decorator";
    public const string UnregisterMessageDecorator = "unregister_message_decorator";
    public const string RuntimeAction = "runtime_action";
    public const string UiRequest = "ui_request";
}

public static class TsBridgeRuntimeActions
{
    public const string GetAllSkills = "get_all_skills";
    public const string GetSelectedSkills = "get_selected_skills";
    public const string SetSelectedSkills = "set_selected_skills";
    public const string GetFlag = "get_flag";
    public const string GetFlags = "get_flags";
    public const string GetActiveTools = "get_active_tools";
    public const string GetAllTools = "get_all_tools";
    public const string GetCommands = "get_commands";
    public const string WaitForIdle = "wait_for_idle";
    public const string NewSession = "new_session";
    public const string ForkSession = "fork_session";
    public const string NavigateTree = "navigate_tree";
    public const string SwitchSession = "switch_session";
    public const string IsIdle = "is_idle";
    public const string HasPendingMessages = "has_pending_messages";
    public const string Compact = "compact";
    public const string GetSystemPrompt = "get_system_prompt";
    public const string Abort = "abort";
    public const string Shutdown = "shutdown";
    public const string Exec = "exec";
    public const string GetThinkingLevel = "get_thinking_level";
    public const string SendMessage = "send_message";
    public const string SendUserMessage = "send_user_message";
    public const string AppendEntry = "append_entry";
    public const string SetEntryLabel = "set_entry_label";
    public const string GetSessionName = "get_session_name";
    public const string SetSessionName = "set_session_name";
    public const string SetActiveTools = "set_active_tools";
    public const string SetModel = "set_model";
    public const string SetThinkingLevel = "set_thinking_level";
    public const string ReloadExtensions = "reload_extensions";
    public const string EmitEvent = "emit_event";
    public const string ListResources = "list_resources";
    public const string ReadResource = "read_resource";
    public const string CompleteSimple = "complete_simple";
    public const string PromptAndWait = "prompt_and_wait";
    public const string CreateAgentSession = "create_agent_session";
    public const string AgentSessionPrompt = "agent_session_prompt";
    public const string AgentSessionSteer = "agent_session_steer";
    public const string AgentSessionFollowUp = "agent_session_follow_up";
    public const string AgentSessionAbort = "agent_session_abort";
    public const string AgentSessionCompact = "agent_session_compact";
    public const string AgentSessionSetModel = "agent_session_set_model";
    public const string AgentSessionSetThinkingLevel = "agent_session_set_thinking_level";
    public const string AgentSessionDispose = "agent_session_dispose";
}

public static class TsBridgeManifestFactory
{
    public static TsBridgeManifest CreateDefault()
        => new(
            TsBridgeManifestSchema.CurrentVersion,
            CreateModuleShims(),
            CreateProtocolManifest(),
            CreateApiSurfaceManifest());

    private static IReadOnlyList<TsBridgeModuleShim> CreateModuleShims()
        => [
            PiCodingAgentShim("@earendil-works/pi-coding-agent", "pisharp-pi-coding-agent-shim.mjs"),
            PiCodingAgentShim("@mariozechner/pi-coding-agent", "pisharp-pi-coding-agent-shim.mjs"),
            PiCodingAgentShim("@pi-coding-agent", "pisharp-pi-coding-agent-shim.mjs"),
            PiAiShim("@earendil-works/pi-ai", "pisharp-earendil-pi-ai-shim.mjs"),
            PiAiShim("@mariozechner/pi-ai", "pisharp-mariozechner-pi-ai-shim.mjs"),
            PiAiShim("@pi-ai", "pisharp-pi-ai-shim.mjs"),
            PiTuiShim("@earendil-works/pi-tui", "pisharp-earendil-pi-tui-shim.mjs"),
            PiTuiShim("@mariozechner/pi-tui", "pisharp-mariozechner-pi-tui-shim.mjs"),
            PiTuiShim("@pi-tui", "pisharp-pi-tui-shim.mjs")
        ];

    private static TsBridgeModuleShim PiAiShim(string specifier, string cacheFileName)
        => new(specifier, cacheFileName, "Pi AI compatibility shim generated from C# bridge metadata.", [
            Namespace("Type", [
                Helper("Any", "typeAny"),
                Helper("Unknown", "typeUnknown"),
                Helper("Null", "typeNull"),
                Helper("String", "typeString"),
                Helper("Number", "typeNumber"),
                Helper("Integer", "typeInteger"),
                Helper("Boolean", "typeBoolean"),
                Helper("Array", "typeArray"),
                Helper("Object", "typeObject"),
                Helper("Record", "typeRecord"),
                Helper("Optional", "typeOptional"),
                Helper("Literal", "typeLiteral"),
                Helper("Union", "typeUnion"),
                Helper("Intersect", "typeIntersect")
            ]),
            Helper("StringEnum", "stringEnum"),
            Helper("Static", "undefinedValue"),
            Helper("getSupportedThinkingLevels", "getSupportedThinkingLevels"),
            Helper("completeSimple", "completeSimple")
        ]);

    private static TsBridgeModuleShim PiTuiShim(string specifier, string cacheFileName)
        => new(specifier, cacheFileName, "Pi TUI compatibility shim generated from C# bridge metadata.", [
            Helper("visibleWidth", "visibleWidth"),
            Helper("truncateToWidth", "truncateToWidth"),
            Helper("wrapTextWithAnsi", "wrapTextWithAnsi"),
            Helper("fuzzyFilter", "fuzzyFilter"),
            Helper("matchesKey", "matchesKey"),
            Helper("Text", "Text"),
            Helper("Container", "Container"),
            Helper("Spacer", "Spacer"),
            Helper("SettingsList", "SettingsList"),
            Helper("Focusable", "Focusable"),
            Helper("Input", "Input"),
            Helper("TUI", "TUI"),
            Json("Key", new Dictionary<string, object?>())
        ]);

    private static TsBridgeModuleShim PiCodingAgentShim(string specifier, string cacheFileName)
        => new(specifier, cacheFileName, "Pi coding-agent compatibility shim generated from C# bridge metadata.",
            ShimExports.Auto.PiCodingAgentExports());

    private static TsBridgeProtocolManifest CreateProtocolManifest()
        => new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(TsBridgeMethods.RegisterTool)] = TsBridgeMethods.RegisterTool,
                [nameof(TsBridgeMethods.RegisterSkill)] = TsBridgeMethods.RegisterSkill,
                [nameof(TsBridgeMethods.RegisterProvider)] = TsBridgeMethods.RegisterProvider,
                [nameof(TsBridgeMethods.UnregisterProvider)] = TsBridgeMethods.UnregisterProvider,
                [nameof(TsBridgeMethods.RegisterCommand)] = TsBridgeMethods.RegisterCommand,
                [nameof(TsBridgeMethods.RegisterShortcut)] = TsBridgeMethods.RegisterShortcut,
                [nameof(TsBridgeMethods.RegisterFlag)] = TsBridgeMethods.RegisterFlag,
                [nameof(TsBridgeMethods.RegisterPromptSection)] = TsBridgeMethods.RegisterPromptSection,
                [nameof(TsBridgeMethods.RegisterPromptTransform)] = TsBridgeMethods.RegisterPromptTransform,
                [nameof(TsBridgeMethods.RegisterMessageRenderer)] = TsBridgeMethods.RegisterMessageRenderer,
                [nameof(TsBridgeMethods.UnregisterMessageRenderer)] = TsBridgeMethods.UnregisterMessageRenderer,
                [nameof(TsBridgeMethods.RegisterMessageDecorator)] = TsBridgeMethods.RegisterMessageDecorator,
                [nameof(TsBridgeMethods.UnregisterMessageDecorator)] = TsBridgeMethods.UnregisterMessageDecorator,
                [nameof(TsBridgeMethods.RuntimeAction)] = TsBridgeMethods.RuntimeAction,
                [nameof(TsBridgeMethods.UiRequest)] = TsBridgeMethods.UiRequest
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(TsBridgeRuntimeActions.GetAllSkills)] = TsBridgeRuntimeActions.GetAllSkills,
                [nameof(TsBridgeRuntimeActions.GetSelectedSkills)] = TsBridgeRuntimeActions.GetSelectedSkills,
                [nameof(TsBridgeRuntimeActions.SetSelectedSkills)] = TsBridgeRuntimeActions.SetSelectedSkills,
                [nameof(TsBridgeRuntimeActions.GetFlag)] = TsBridgeRuntimeActions.GetFlag,
                [nameof(TsBridgeRuntimeActions.GetFlags)] = TsBridgeRuntimeActions.GetFlags,
                [nameof(TsBridgeRuntimeActions.GetActiveTools)] = TsBridgeRuntimeActions.GetActiveTools,
                [nameof(TsBridgeRuntimeActions.GetAllTools)] = TsBridgeRuntimeActions.GetAllTools,
                [nameof(TsBridgeRuntimeActions.GetCommands)] = TsBridgeRuntimeActions.GetCommands,
                [nameof(TsBridgeRuntimeActions.WaitForIdle)] = TsBridgeRuntimeActions.WaitForIdle,
                [nameof(TsBridgeRuntimeActions.NewSession)] = TsBridgeRuntimeActions.NewSession,
                [nameof(TsBridgeRuntimeActions.ForkSession)] = TsBridgeRuntimeActions.ForkSession,
                [nameof(TsBridgeRuntimeActions.NavigateTree)] = TsBridgeRuntimeActions.NavigateTree,
                [nameof(TsBridgeRuntimeActions.SwitchSession)] = TsBridgeRuntimeActions.SwitchSession,
                [nameof(TsBridgeRuntimeActions.IsIdle)] = TsBridgeRuntimeActions.IsIdle,
                [nameof(TsBridgeRuntimeActions.HasPendingMessages)] = TsBridgeRuntimeActions.HasPendingMessages,
                [nameof(TsBridgeRuntimeActions.Compact)] = TsBridgeRuntimeActions.Compact,
                [nameof(TsBridgeRuntimeActions.GetSystemPrompt)] = TsBridgeRuntimeActions.GetSystemPrompt,
                [nameof(TsBridgeRuntimeActions.Abort)] = TsBridgeRuntimeActions.Abort,
                [nameof(TsBridgeRuntimeActions.Shutdown)] = TsBridgeRuntimeActions.Shutdown,
                [nameof(TsBridgeRuntimeActions.Exec)] = TsBridgeRuntimeActions.Exec,
                [nameof(TsBridgeRuntimeActions.GetThinkingLevel)] = TsBridgeRuntimeActions.GetThinkingLevel,
                [nameof(TsBridgeRuntimeActions.SendMessage)] = TsBridgeRuntimeActions.SendMessage,
                [nameof(TsBridgeRuntimeActions.SendUserMessage)] = TsBridgeRuntimeActions.SendUserMessage,
                [nameof(TsBridgeRuntimeActions.AppendEntry)] = TsBridgeRuntimeActions.AppendEntry,
                [nameof(TsBridgeRuntimeActions.SetEntryLabel)] = TsBridgeRuntimeActions.SetEntryLabel,
                [nameof(TsBridgeRuntimeActions.GetSessionName)] = TsBridgeRuntimeActions.GetSessionName,
                [nameof(TsBridgeRuntimeActions.SetSessionName)] = TsBridgeRuntimeActions.SetSessionName,
                [nameof(TsBridgeRuntimeActions.SetActiveTools)] = TsBridgeRuntimeActions.SetActiveTools,
                [nameof(TsBridgeRuntimeActions.SetModel)] = TsBridgeRuntimeActions.SetModel,
                [nameof(TsBridgeRuntimeActions.SetThinkingLevel)] = TsBridgeRuntimeActions.SetThinkingLevel,
                [nameof(TsBridgeRuntimeActions.ReloadExtensions)] = TsBridgeRuntimeActions.ReloadExtensions,
                [nameof(TsBridgeRuntimeActions.EmitEvent)] = TsBridgeRuntimeActions.EmitEvent,
                [nameof(TsBridgeRuntimeActions.ListResources)] = TsBridgeRuntimeActions.ListResources,
                [nameof(TsBridgeRuntimeActions.ReadResource)] = TsBridgeRuntimeActions.ReadResource,
                [nameof(TsBridgeRuntimeActions.CompleteSimple)] = TsBridgeRuntimeActions.CompleteSimple,
                [nameof(TsBridgeRuntimeActions.PromptAndWait)] = TsBridgeRuntimeActions.PromptAndWait,
                [nameof(TsBridgeRuntimeActions.CreateAgentSession)] = TsBridgeRuntimeActions.CreateAgentSession,
                [nameof(TsBridgeRuntimeActions.AgentSessionPrompt)] = TsBridgeRuntimeActions.AgentSessionPrompt,
                [nameof(TsBridgeRuntimeActions.AgentSessionSteer)] = TsBridgeRuntimeActions.AgentSessionSteer,
                [nameof(TsBridgeRuntimeActions.AgentSessionFollowUp)] = TsBridgeRuntimeActions.AgentSessionFollowUp,
                [nameof(TsBridgeRuntimeActions.AgentSessionAbort)] = TsBridgeRuntimeActions.AgentSessionAbort,
                [nameof(TsBridgeRuntimeActions.AgentSessionCompact)] = TsBridgeRuntimeActions.AgentSessionCompact,
                [nameof(TsBridgeRuntimeActions.AgentSessionSetModel)] = TsBridgeRuntimeActions.AgentSessionSetModel,
                [nameof(TsBridgeRuntimeActions.AgentSessionSetThinkingLevel)] = TsBridgeRuntimeActions.AgentSessionSetThinkingLevel,
                [nameof(TsBridgeRuntimeActions.AgentSessionDispose)] = TsBridgeRuntimeActions.AgentSessionDispose
            });

    private static TsBridgeApiSurfaceManifest CreateApiSurfaceManifest()
    {
        var members = new List<TsBridgeApiMember>
        {
            Snapshot("pi", "getCommands", "function", snapshotField: "commands"),
            Runtime("pi", "setSessionName", "function", TsBridgeRuntimeActions.SetSessionName),
            Snapshot("pi", "getSessionName", "function", snapshotField: "session.sessionName"),
            Runtime("pi", "setLabel", "function", TsBridgeRuntimeActions.SetEntryLabel),
            Runtime("ctx", "waitForIdle", "function", TsBridgeRuntimeActions.WaitForIdle),
            Runtime("ctx", "newSession", "function", TsBridgeRuntimeActions.NewSession),
            Runtime("replacementCtx", "sendMessage", "function", TsBridgeRuntimeActions.SendMessage),
            Runtime("replacementCtx", "sendUserMessage", "function", TsBridgeRuntimeActions.SendUserMessage),
            Snapshot("ctx.sessionManager", "getBranch", "function", snapshotField: "session.branch"),
            Snapshot("ctx.sessionManager", "getEntries", "function", snapshotField: "session.entries"),
            Snapshot("ctx.sessionManager", "getLeafId", "function", snapshotField: "session.leafId"),
            Snapshot("ctx.sessionManager", "getSessionFile", "function", snapshotField: "session.sessionFile"),
            Snapshot("ctx.sessionManager", "getSessionName", "function", snapshotField: "session.sessionName"),
            Runtime("pi", "getFlag", "function", TsBridgeRuntimeActions.GetFlag),
            Runtime("pi", "getFlags", "function", TsBridgeRuntimeActions.GetFlags),
            Runtime("pi", "getActiveTools", "function", TsBridgeRuntimeActions.GetActiveTools),
            Runtime("pi", "getAllTools", "function", TsBridgeRuntimeActions.GetAllTools),
            Runtime("pi", "getThinkingLevel", "function", TsBridgeRuntimeActions.GetThinkingLevel),
            Runtime("pi", "sendMessage", "function", TsBridgeRuntimeActions.SendMessage),
            Runtime("pi", "sendUserMessage", "function", TsBridgeRuntimeActions.SendUserMessage),
            Runtime("pi", "setActiveTools", "function", TsBridgeRuntimeActions.SetActiveTools),
            Runtime("pi", "setModel", "function", TsBridgeRuntimeActions.SetModel),
            Runtime("pi", "setThinkingLevel", "function", TsBridgeRuntimeActions.SetThinkingLevel),
            Runtime("pi", "reload", "function", TsBridgeRuntimeActions.ReloadExtensions),
            Runtime("pi.resources", "list", "function", TsBridgeRuntimeActions.ListResources),
            Runtime("pi.resources", "read", "function", TsBridgeRuntimeActions.ReadResource),
            Runtime("@pi-ai", "completeSimple", "function", TsBridgeRuntimeActions.CompleteSimple),
            Runtime("@pi-coding-agent", "createAgentSession", "function", TsBridgeRuntimeActions.CreateAgentSession),
            Runtime("ctx", "fork", "function", TsBridgeRuntimeActions.ForkSession),
            Runtime("ctx", "navigateTree", "function", TsBridgeRuntimeActions.NavigateTree),
            Runtime("ctx", "switchSession", "function", TsBridgeRuntimeActions.SwitchSession),
            Runtime("ctx", "reload", "function", TsBridgeRuntimeActions.ReloadExtensions),
            Snapshot("ctx.sessionManager", "getLeafEntry", "function", snapshotField: "session.leafEntry"),
            Snapshot("ctx.sessionManager", "getEntry", "function", snapshotField: "session.entries"),
            Snapshot("ctx.sessionManager", "getTree", "function", snapshotField: "session.tree"),
            Snapshot("ctx.sessionManager", "getChildren", "function", snapshotField: "session.childrenByParentId"),
            Snapshot("ctx.sessionManager", "getLabel", "function", snapshotField: "session.labels"),
            Snapshot("ctx.sessionManager", "getHeader", "function", snapshotField: "session.header"),
            Snapshot("ctx.sessionManager", "getCwd", "function", snapshotField: "session.cwd"),
            Snapshot("ctx.sessionManager", "getSessionDir", "function", snapshotField: "session.sessionDir"),
            Snapshot("ctx.sessionManager", "isPersisted", "function", snapshotField: "session.isPersisted"),
            Runtime("pi", "exec", "function", TsBridgeRuntimeActions.Exec),
            Snapshot("ctx", "modelRegistry", "property", snapshotField: "modelRegistry"),
            Snapshot("ctx", "model", "property", snapshotField: "model"),
            Runtime("ctx", "isIdle", "function", TsBridgeRuntimeActions.IsIdle),
            Runtime("ctx", "abort", "function", TsBridgeRuntimeActions.Abort),
            Runtime("ctx", "shutdown", "function", TsBridgeRuntimeActions.Shutdown),
            Runtime("ctx", "hasPendingMessages", "function", TsBridgeRuntimeActions.HasPendingMessages),
            Snapshot("ctx", "getContextUsage", "function", snapshotField: "session.contextUsage"),
            Runtime("ctx", "compact", "function", TsBridgeRuntimeActions.Compact),
            Runtime("ctx", "getSystemPrompt", "function", TsBridgeRuntimeActions.GetSystemPrompt),
            Runtime("pi", "registerTool", "function", TsBridgeMethods.RegisterTool),
            Runtime("pi", "registerCommand", "function", TsBridgeMethods.RegisterCommand),
            Runtime("pi", "registerShortcut", "function", TsBridgeMethods.RegisterShortcut),
            Runtime("pi", "registerFlag", "function", TsBridgeMethods.RegisterFlag),
            Runtime("pi.prompt", "registerSection", "function", TsBridgeMethods.RegisterPromptSection),
            Runtime("pi.prompt", "registerTransform", "function", TsBridgeMethods.RegisterPromptTransform),
            Runtime("pi", "registerProvider", "function", TsBridgeMethods.RegisterProvider),
            Runtime("pi", "registerMessageRenderer", "function", TsBridgeMethods.RegisterMessageRenderer),
            Runtime("pi", "registerMessageDecorator", "function", TsBridgeMethods.RegisterMessageDecorator),
            Implemented("ctx.ui", "editor", "property"),
            Implemented("ctx.ui", "theme", "property"),
            Implemented("ctx.ui", "customComponent", "function"),
            Implemented("ctx.ui", "custom", "function"),
            Implemented("ctx.ui", "workingIndicator", "function"),
            Implemented("ctx.ui", "registerMenuItem", "function"),
            Implemented("tool", "prepareArguments", "function"),
            Implemented("tool", "executionMode", "property")
        };

        var snapshotFields = new List<TsBridgeRuntimeSnapshotField>
        {
            new("commands", "ExtensionCommandInfo[]", true, true),
            new("session.sessionName", "string?", false, true),
            new("session.branch", "object[]", false, true),
            new("session.entries", "object[]", false, true),
            new("session.leafId", "string?", false, true),
            new("session.sessionFile", "string?", false, true),
            new("session.sessionId", "string?", false, true),
            new("session.leafEntry", "object?", false, true),
            new("session.tree", "object", false, true),
            new("session.childrenByParentId", "object", false, true),
            new("session.labels", "object", false, true),
            new("session.header", "object", false, true),
            new("session.cwd", "string", false, true),
            new("session.sessionDir", "string?", false, true),
            new("session.isPersisted", "bool", false, true),
            new("session.contextUsage", "object?", false, true),
            new("flags", "object", false, true),
            new("activeTools", "string[]", false, true),
            new("allTools", "string[]", false, true),
            new("thinkingLevel", "string?", false, true),
            new("model", "ModelDescriptor", false, true),
            new("modelRegistry", "object", false, true)
        };

        var events = new List<TsBridgeEventContract>
        {
            new("session_start", "SessionStart payload", "Extension context", false, false, false, TsBridgeApiMemberStatuses.Implemented),
            new("before_agent_start", "BeforeAgentStart payload", "Extension context", true, true, false, TsBridgeApiMemberStatuses.Implemented),
            new("before_prompt_render", "Prompt document payload", "Extension context", true, true, false, TsBridgeApiMemberStatuses.Implemented),
            new("tool_call", "Tool call payload", "Extension context", true, true, false, TsBridgeApiMemberStatuses.Implemented),
            new("tool_result", "Tool result payload", "Extension context", false, true, true, TsBridgeApiMemberStatuses.Implemented),
            new("input_transform", "Input transform payload", "Extension context", true, true, true, TsBridgeApiMemberStatuses.Implemented)
        };

        return new TsBridgeApiSurfaceManifest(members, snapshotFields, events);
    }

    private static TsBridgeShimExport Helper(string name, string helper)
        => new(name, TsBridgeShimExportKinds.Helper, Helper: helper);

    private static TsBridgeShimExport Json(string name, object? value)
        => new(name, TsBridgeShimExportKinds.JsonConst, Value: value);

    private static TsBridgeShimExport Unavailable(string name, string message)
        => new(name, TsBridgeShimExportKinds.UnavailableFunction, Message: message);

    private static TsBridgeShimExport AsyncUnavailable(string name, string message)
        => new(name, TsBridgeShimExportKinds.AsyncUnavailableFunction, Message: message);

    private static TsBridgeShimExport Namespace(string name, IReadOnlyList<TsBridgeShimExport> members)
        => new(name, TsBridgeShimExportKinds.Namespace, Members: members);

    private static TsBridgeShimExport RuntimeFunction(string name, string runtimeAction)
        => new(name, TsBridgeShimExportKinds.RuntimeFunction, RuntimeAction: runtimeAction);

    private static TsBridgeApiMember Runtime(string surface, string name, string kind, string runtimeAction)
        => new(surface, name, kind, TsBridgeApiMemberStatuses.RuntimeAction, RuntimeAction: runtimeAction);

    private static TsBridgeApiMember Snapshot(string surface, string name, string kind, string snapshotField)
        => new(surface, name, kind, TsBridgeApiMemberStatuses.Snapshot, SnapshotField: snapshotField);

    private static TsBridgeApiMember Implemented(string surface, string name, string kind)
        => new(surface, name, kind, TsBridgeApiMemberStatuses.Implemented);
}
