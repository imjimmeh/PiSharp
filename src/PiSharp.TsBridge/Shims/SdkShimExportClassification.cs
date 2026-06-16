using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge.Shims;

public enum SdkShimExportStatus
{
    Unclassified,
    Implemented,
    Stubbed,
    Unsupported
}

public sealed record SdkShimExportDefinition(
    SdkShimExportStatus Status,
    string ExportKind,
    string? Helper = null,
    object? Value = null,
    string? RuntimeAction = null,
    string? Message = null);

public static class SdkShimExportClassification
{
    public static readonly IReadOnlyDictionary<string, SdkShimExportDefinition> All =
        new Dictionary<string, SdkShimExportDefinition>(StringComparer.Ordinal)
        {
            ["DEFAULT_MAX_BYTES"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.JsonConst, Value: 100_000),
            ["DEFAULT_MAX_LINES"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.JsonConst, Value: 2_000),

            ["isToolCallEventType"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "isToolCallEventType"),
            ["isEditToolResult"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "isEditToolResult"),
            ["isWriteToolResult"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "isWriteToolResult"),
            ["getAgentDir"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "getAgentDir"),
            ["parseSkillBlock"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "parseSkillBlock"),
            ["highlightCode"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "stringValue"),
            ["keyHint"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "stringValue"),

            ["truncateTail"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "truncateTail"),
            ["truncateHead"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "truncateHead"),
            ["formatSize"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "formatSize"),
            ["parseFrontmatter"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "parseFrontmatter"),
            ["stripFrontmatter"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "stripFrontmatter"),
            ["buildSessionContext"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "identityOrEmptyObject"),
            ["convertToLlm"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "identity"),
            ["copyToClipboard"] = new(SdkShimExportStatus.Unsupported, TsBridgeShimExportKinds.UnavailableFunction, Message: "Pi coding-agent SDK export 'copyToClipboard' is not implemented by PiSharp."),
            ["defineTool"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "identity"),

            ["createAgentSession"] = new(SdkShimExportStatus.Implemented, TsBridgeShimExportKinds.Helper, Helper: "createAgentSession", RuntimeAction: TsBridgeRuntimeActions.CreateAgentSession),

            ["getMarkdownTheme"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyObjectFunction"),
            ["getSettingsListTheme"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyObjectFunction"),

            ["createCodingTools"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createReadOnlyTools"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createBashTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createEditTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createFindTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createGrepTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createLsTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createReadTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),
            ["createWriteTool"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "emptyArrayFunction"),

            ["DynamicBorder"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "DynamicBorder"),
            ["DefaultResourceLoader"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "DefaultResourceLoader"),
            ["SessionManager"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "SessionManager"),
            ["SettingsManager"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "SettingsManager"),
            ["AgentSession"] = new(SdkShimExportStatus.Stubbed, TsBridgeShimExportKinds.Helper, Helper: "AgentSession"),
        };
}
