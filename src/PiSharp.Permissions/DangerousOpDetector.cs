using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Abstractions.Environment;

namespace PiSharp.Permissions;

/// <summary>
/// Classifies a tool call against the built-in dangerous-default table (P29 §8):
/// <c>bash</c> (ask), <c>gitPush</c> (ask, with a precise reason), <c>writeOutsideCwd</c>
/// (deny), <c>writeOverwrite</c> (ask), and <c>none</c> (allow). The classification is a
/// code table so it cannot be loosened via settings.
/// </summary>
public static class DangerousOpDetector
{
    public const string Bash = "bash";
    public const string GitPush = "gitPush";
    public const string WriteOutsideCwd = "writeOutsideCwd";
    public const string WriteOverwrite = "writeOverwrite";
    public const string None = "none";

    /// <summary>Matches a bash command containing a git push (incl. --force).</summary>
    public static readonly Regex GitPushPattern = new(@"\bgit\s+push\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Matches a bash command containing a hard git reset.</summary>
    public static readonly Regex GitResetHardPattern = new(@"\bgit\s+reset\s+--hard\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Matches a bash command containing a recursive force rm.</summary>
    public static readonly Regex RmRfPattern = new(@"\brm\s+-[a-zA-Z]*r[a-zA-Z]*f\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Sync, pure classification core. <paramref name="resolvedPath"/> is the absolute target
    /// path for write/edit tools (null when it could not be resolved) and
    /// <paramref name="overwriteTarget"/> reports whether that path already exists.
    /// </summary>
    public static string Category(string tool, string serializedArgs, string? resolvedPath, string? cwd, bool overwriteTarget = false)
    {
        switch (tool.ToLowerInvariant())
        {
            case "bash":
                var command = ExtractStringArg(serializedArgs, "command");
                if (string.IsNullOrWhiteSpace(command)) return Bash;
                return BashCategoryOf(command);

            case "write":
            case "edit":
                if (resolvedPath is null) return None;
                if (IsOutsideCwd(resolvedPath, cwd)) return WriteOutsideCwd;
                if (tool.Equals("write", StringComparison.OrdinalIgnoreCase) && overwriteTarget) return WriteOverwrite;
                return None;

            default:
                return None;
        }
    }

    /// <summary>
    /// Async facade used by the middleware: resolves the write/edit target path via the
    /// execution file system (when available) and probes overwrite before classifying.
    /// </summary>
    public static async Task<string> CategoryAsync(
        string tool,
        JsonElement args,
        IFileSystem? fileSystem,
        CancellationToken cancellationToken = default)
    {
        if (fileSystem is null) return Category(tool, Serialize(args), null, null, false);

        if (tool.Equals("write", StringComparison.OrdinalIgnoreCase) || tool.Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            var path = ExtractStringArg(args, "path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var absolute = await TryResolvePathAsync(fileSystem, path, cancellationToken).ConfigureAwait(false);
                if (absolute is not null)
                {
                    var exists = await fileSystem.ExistsAsync(absolute, cancellationToken).ConfigureAwait(false);
                    var overwrite = exists.IsOk && exists.Value;
                    return Category(tool, Serialize(args), absolute, fileSystem.Cwd, overwrite);
                }
            }
        }

        return Category(tool, Serialize(args), null, fileSystem.Cwd, false);
    }

    /// <summary>Classifies a bash command string: git-push/reset/rm-rf categories vs plain bash.</summary>
    public static string BashCategoryOf(string command)
    {
        if (GitPushPattern.IsMatch(command)) return GitPush;
        if (GitResetHardPattern.IsMatch(command) || RmRfPattern.IsMatch(command)) return GitPush;
        return Bash;
    }
    public static bool IsOutsideCwd(string absolutePath, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return true;
        var normalizedPath = NormalizeResolved(absolutePath);
        var normalizedCwd = NormalizeResolved(cwd);
        if (normalizedPath.Equals(normalizedCwd, StringComparison.OrdinalIgnoreCase)) return false;
        return !normalizedPath.StartsWith(normalizedCwd + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Resolves relative segments (. and ..) before normalization so traversal cannot bypass containment.</summary>
    private static string NormalizeResolved(string path)
    {
        try
        {
            return Normalize(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return Normalize(path);
        }
    }

    internal static string Serialize(JsonElement args)
    {
        try
        {
            var raw = args.GetRawText();
            if (!string.IsNullOrEmpty(raw)) return raw;
        }
        catch (InvalidOperationException)
        {
            // Not backed by a document — fall through to full serialization.
        }
        return JsonSerializer.Serialize(args);
    }

    private static async Task<string?> TryResolvePathAsync(IFileSystem fileSystem, string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await fileSystem.AbsolutePathAsync(path, cancellationToken).ConfigureAwait(false);
            return result.IsOk ? result.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExtractStringArg(string serializedArgs, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(serializedArgs);
            return ExtractStringArg(document.RootElement, property);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractStringArg(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out var element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
