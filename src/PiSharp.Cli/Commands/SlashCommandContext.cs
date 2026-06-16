using PiSharp.Abstractions.Sessions;
using PiSharp.Ai.Auth;
using PiSharp.Runtime;

namespace PiSharp.Cli.Commands;

public sealed record SlashCommandContext(
    string CommandName,
    SessionRuntime Runtime,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>> SelectAsync,
    Func<string, CancellationToken, Task<string?>> InputAsync,
    Func<string, CancellationToken, Task> NotifyAsync,
    IOAuthStorage? OAuthStorage = null,
    Func<string, CancellationToken, Task>? SubmitPromptAsync = null,
    Func<Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>, Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>, JsonlSessionMetadata?, CancellationToken, Task<JsonlSessionMetadata?>>? SelectSessionMetadataAsync = null,
    Func<string, CancellationToken, Task>? OpenUrlAsync = null)
{
    public async Task SubmitPromptAsyncOrDefault(string text, CancellationToken token)
    {
        if (SubmitPromptAsync is not null) await SubmitPromptAsync(text, token);
        else await Runtime.Harness.PromptAsync(text, token);
    }

    public string SessionChangeCancelledMessage(string operation, string? reason)
        => string.IsNullOrWhiteSpace(reason) ? $"{operation} cancelled by extension." : $"{operation} cancelled by extension: {reason}";

    public bool IsPathUnderAllowedRoot(string path)
    {
        var sessionDir = Path.GetFullPath(Path.GetDirectoryName(Runtime.Session.Metadata.Path) ?? ".");
        var tempDir = Path.GetFullPath(Path.GetTempPath());
        var resolved = Path.GetFullPath(path);
        return IsUnderRoot(resolved, sessionDir) || IsUnderRoot(resolved, tempDir);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var fullPath = Path.TrimEndingDirectorySeparator(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
