namespace PiSharp.Ai.Auth;

/// <summary>
/// Shared constants for GitHub Copilot integrations: the spoofed editor headers
/// required by the Copilot chat endpoint and the default individual-account base URL.
/// Values mirror the previous private constants in <see cref="GitHubCopilotOAuthProvider"/>.
/// </summary>
internal static class CopilotConstants
{
    public static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat",
    };

    public const string DefaultBaseUrl = "https://api.individual.githubcopilot.com";
}
