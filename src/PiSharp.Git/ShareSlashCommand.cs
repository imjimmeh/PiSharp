using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;

namespace PiSharp.Git;

/// <summary>
/// The <c>/share</c> slash command (upgraded from the removed built-in File.Copy to a
/// private GitHub gist upload, with a <c>--local</c> fallback preserving the legacy copy).
///
/// Grammar: <c>/share [&lt;path&gt;] [--local &lt;target&gt;] [--public] [--yes]</c>.
/// The no-arg <c>/share</c> (upload the current session file) is phase-gated on the
/// optional <c>IExtensionSessionApi.GetSessionFilePathAsync</c> core change (C3), which
/// is out of batch scope — until it lands, this form errors and points at <c>/share &lt;path&gt;</c>.
/// </summary>
public sealed class ShareSlashCommand(
    CommandHost host,
    IGistUploader uploader,
    GistTokenResolver tokenResolver,
    GitPluginOptions options)
{
    /// <summary>Raised after a successful gist upload.</summary>
    public event Action<ShareCompletedEvent>? ShareCompleted;

    public async Task HandleAsync(string args, CancellationToken cancellationToken = default)
    {
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            await NotifyAsync("Usage: /share [<path>] [--local <target-path>] [--public] [--yes]", true);
            return;
        }

        // [optional: not implemented — C3 GetSessionFilePathAsync touches src/PiSharp.Extensions (out of batch
        // scope); no-arg /share (upload the current session JSONL) requires a path until that core change lands.]
        if (parsed.Path is null && parsed.LocalTarget is null)
        {
            await NotifyAsync(
                "Usage: /share <path> [--public] [--yes]. Uploading the current session automatically requires " +
                "a core change that is not yet available; pass a file path explicitly.", true);
            return;
        }

        if (parsed.LocalTarget is not null)
        {
            await ShareLocalAsync(parsed, cancellationToken);
            return;
        }

        await ShareGistAsync(parsed, cancellationToken);
    }

    private async Task ShareGistAsync(ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var path = parsed.Path!;
        if (!IsPathUnderAllowedRoot(path))
        {
            await NotifyAsync("Share path must be under the session or temp directory.", true);
            return;
        }

        if (!File.Exists(path))
        {
            await NotifyAsync($"File not found: {path}", true);
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);

        var tokenResolution = await tokenResolver.ResolveAsync(cancellationToken);
        if (!tokenResolution.Success)
        {
            await NotifyAsync(tokenResolution.Error ?? "No GitHub token available.", true);
            return;
        }

        var isPublic = parsed.IsPublic || string.Equals(options.ShareVisibility, "public", StringComparison.OrdinalIgnoreCase);
        var confirm = !parsed.Yes && host.HasUi;
        if (confirm)
        {
            var ok = await host.Ui.ConfirmAsync(
                $"Upload {Path.GetFileName(path)} ({bytes} bytes) as a {(isPublic ? "public" : "private")} gist?",
                cancellationToken);
            if (!ok)
            {
                await NotifyAsync("Share cancelled.", false);
                return;
            }
        }

        var fileName = options.ShareFileName is { Length: > 0 }
            ? SanitizeFileName(options.ShareFileName)
            : SanitizeFileName(Path.GetFileName(path));
        var result = await uploader.UploadAsync(new GistUploadRequest(
            fileName, content, isPublic, options.ShareDescription, tokenResolution.Token!), cancellationToken);

        if (!result.Success)
        {
            await NotifyAsync(result.Error ?? "Gist upload failed.", true);
            return;
        }
        await host.SendMessageAsync($"Shared as gist: {result.HtmlUrl}", cancellationToken);
        ShareCompleted?.Invoke(new ShareCompletedEvent(result.HtmlUrl!, result.GistId!, fileName, result.Bytes ?? 0));
    }

    private async Task ShareLocalAsync(ParsedArgs parsed, CancellationToken cancellationToken)
    {
        // Legacy behavior (File.Copy) preserved behind --local. Without C3 the source is
        // an explicit path rather than the session file.
        var source = parsed.Path;
        if (source is null)
        {
            await NotifyAsync("Usage: /share <source-path> --local <target-path>", true);
            return;
        }

        if (!File.Exists(source))
        {
            await NotifyAsync($"File not found: {source}", true);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(parsed.LocalTarget!) ?? ".");
        File.Copy(source, parsed.LocalTarget!, overwrite: true);
        await NotifyAsync($"Session shared to {parsed.LocalTarget}", false);
    }

    private bool IsPathUnderAllowedRoot(string path)
    {
        var sessionsRoot = PiAgentPaths.FromCwd(host.Cwd).SessionsRoot;
        var tempRoot = Path.GetTempPath();
        var resolved = Path.GetFullPath(path);
        return IsUnderRoot(resolved, sessionsRoot) || IsUnderRoot(resolved, tempRoot);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task NotifyAsync(string message, bool isError)
    {
        try
        {
            await host.Ui.NotifyAsync(message, isError ? ExtensionUiSeverity.Error : ExtensionUiSeverity.Success);
        }
        catch (NotSupportedException)
        {
            // No UI (print/rpc): the message is surfaced through the command result instead.
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var chars = fileName.Select(c => (char.IsLetterOrDigit(c) || c is '.' or '-' or '_') ? c : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return sanitized.Length == 0 ? "share.txt" : sanitized;
    }

    private static ParsedArgs? ParseArgs(string args)
    {
        var tokens = Tokenize(args);
        string? path = null;
        string? localTarget = null;
        var isPublic = false;
        var yes = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case "--local":
                    if (i + 1 >= tokens.Count)
                    {
                        return null;
                    }

                    localTarget = tokens[++i];
                    break;
                case "--public":
                    isPublic = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                default:
                    if (path is null)
                    {
                        path = tokens[i];
                    }
                    else
                    {
                        return null; // unexpected extra positional
                    }

                    break;
            }
        }

        return new ParsedArgs(path, localTarget, isPublic, yes);
    }

    private static IReadOnlyList<string> Tokenize(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in (args ?? string.Empty).Trim())
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private sealed record ParsedArgs(string? Path, string? LocalTarget, bool IsPublic, bool Yes);
}
