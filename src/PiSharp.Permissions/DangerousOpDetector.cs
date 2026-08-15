using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Environment;

namespace PiSharp.Permissions;

/// <summary>
/// Classifies a tool call against the built-in dangerous-default table (P29 §8):
/// <c>bash</c> (ask), <c>gitPush</c> (ask), <c>rmRf</c> (ask), <c>writeOutsideCwd</c> (deny),
/// <c>writeOverwrite</c> (ask), <c>mcpSpawn</c> (ask — MCP tool carrying a command), and
/// <c>unknown</c> (ask — any unlisted tool fails closed). The classification is a code table
/// so it cannot be loosened via settings.
/// </summary>
public static class DangerousOpDetector
{
    public const string Bash = "bash";
    public const string GitPush = "gitPush";
    public const string RmRf = "rmRf";
    public const string WriteOutsideCwd = "writeOutsideCwd";
    public const string WriteOverwrite = "writeOverwrite";
    public const string McpSpawn = "mcpSpawn";
    public const string Unknown = "unknown";
    public const string None = "none";

    /// <summary>
    /// Built-in read-only / benign tools that need no gate: keeping these at
    /// <see cref="None"/> preserves the historic allow-by-default posture for the host's own
    /// safe surface while every other (extension / custom / MCP) tool fails closed.
    /// </summary>
    private static readonly HashSet<string> KnownSafeReadOnlyTools = new(
        ["read", "grep", "find", "ls", "hashlines", "ast_grep", "yield", "task"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Arg keys that indicate a tool touches the filesystem; resolved against cwd.</summary>
    private static readonly string[] PathLikeArgKeys = ["path", "file", "directory", "cwd"];

    /// <summary>Arg keys that indicate a tool can execute a command (a spawn gate).</summary>
    private static readonly string[] ExecArgKeys = ["command", "exec"];

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
                return ClassifyUnlisted(tool, serializedArgs, resolvedPath, cwd);
        }
    }

    /// <summary>
    /// Async facade used by the middleware: resolves write/edit target paths and, for unlisted
    /// tools, resolves any filesystem argument via the execution file system (when available)
    /// so a path that escapes cwd fails closed even on custom tools.
    /// </summary>
    public static async Task<string> CategoryAsync(
        string tool,
        JsonElement args,
        IFileSystem? fileSystem,
        CancellationToken cancellationToken = default)
    {
        if (fileSystem is null) return Category(tool, Serialize(args), null, null, false);

        var isWriteEdit = tool.Equals("write", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("edit", StringComparison.OrdinalIgnoreCase);
        var isBash = tool.Equals("bash", StringComparison.OrdinalIgnoreCase);
        if (isWriteEdit || isBash || IsUnlisted(tool))
        {
            var path = ExtractStringArg(args, "path");
            if (string.IsNullOrWhiteSpace(path) && !isBash)
                path = ExtractStringArg(args, "file") ?? ExtractStringArg(args, "directory");

            if (!string.IsNullOrWhiteSpace(path))
            {
                var absolute = await TryResolvePathAsync(fileSystem, path, cancellationToken).ConfigureAwait(false);
                if (absolute is not null)
                {
                    if (isWriteEdit)
                    {
                        var exists = await fileSystem.ExistsAsync(absolute, cancellationToken).ConfigureAwait(false);
                        var overwrite = exists.IsOk && exists.Value;
                        return Category(tool, Serialize(args), absolute, fileSystem.Cwd, overwrite);
                    }
                    return Category(tool, Serialize(args), absolute, fileSystem.Cwd, false);
                }
            }
        }

        return Category(tool, Serialize(args), null, fileSystem.Cwd, false);
    }

    /// <summary>
    /// Classifies a bash command string by tokenizing it (whitespace- and quote-aware) and
    /// matching command verbs + flag shapes: git push / git reset --hard and rm with any
    /// combination of recursive (-r/--recursive) and force (-f/--force) flags produce a
    /// distinct destructive category; anything else is plain bash.
    /// </summary>
    public static string BashCategoryOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Bash;
        var tokens = Tokenize(command);
        if (HasGitPush(tokens)) return GitPush;
        if (HasGitResetHard(tokens)) return GitPush;
        if (IsRmRecursiveForce(tokens)) return RmRf;
        return Bash;
    }

    private static string ClassifyUnlisted(string tool, string serializedArgs, string? resolvedPath, string? cwd)
    {
        if (KnownSafeReadOnlyTools.Contains(tool)) return None;

        var hasExecArg = HasAnyStringArg(serializedArgs, ExecArgKeys);
        if (IsMcpTool(tool) && hasExecArg) return McpSpawn;

        if (ResolvedPathIsOutside(serializedArgs, resolvedPath, cwd)) return WriteOutsideCwd;
        return Unknown;
    }

    /// <summary>Only genuinely unlisted tools benefit from the conservative schema scan.</summary>
    private static bool IsUnlisted(string tool)
    {
        var lower = tool.ToLowerInvariant();
        return lower is not ("bash" or "write" or "edit")
            && !KnownSafeReadOnlyTools.Contains(lower);
    }

    private static bool ResolvedPathIsOutside(string serializedArgs, string? resolvedPath, string? cwd)
    {
        var raw = resolvedPath ?? ExtractPathLikeArg(serializedArgs);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var absolute = ResolveAgainstCwd(raw, cwd);
        return absolute is not null && IsOutsideCwd(absolute, cwd);
    }

    private static string? ResolveAgainstCwd(string path, string? cwd)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(cwd ?? string.Empty, path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsMcpTool(string tool)
    {
        var dot = tool.IndexOf('.');
        return dot > 0 && tool.AsSpan(0, dot).Equals("mcp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractPathLikeArg(string serializedArgs)
    {
        foreach (var key in PathLikeArgKeys)
        {
            var value = ExtractStringArg(serializedArgs, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool HasAnyStringArg(string serializedArgs, string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ExtractStringArg(serializedArgs, key);
            if (!string.IsNullOrWhiteSpace(value)) return true;
        }
        return false;
    }

    /// <summary>
    /// Tokenizes a shell command respecting single/double quotes and backslash escapes.
    /// The command is not executed — this is purely defensive string analysis.
    /// </summary>
    internal static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaped = false;

        foreach (var ch in command)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }

            if (quote is not null)
            {
                if (ch == quote) quote = null;
                else current.Append(ch);
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }

            current.Append(ch);
        }

        if (escaped) current.Append('\\');
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static bool HasGitPush(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Equals("git", StringComparison.OrdinalIgnoreCase)) continue;
            for (var j = i + 1; j < tokens.Count; j++)
                if (tokens[j].Equals("push", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static bool HasGitResetHard(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Equals("git", StringComparison.OrdinalIgnoreCase)) continue;
            for (var j = i + 1; j < tokens.Count - 1; j++)
                if (tokens[j].Equals("reset", StringComparison.OrdinalIgnoreCase)
                    && tokens[j + 1].Equals("--hard", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Detects <c>rm</c> with both a recursive and a force flag in any order/form:
    /// <c>rm -rf</c>, <c>rm -fr</c>, <c>rm -r -f</c>, <c>rm -rfv</c>,
    /// <c>rm --recursive --force</c>. Flag tokens before the <c>rm</c> verb are ignored.
    /// </summary>
    private static bool IsRmRecursiveForce(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Equals("rm", StringComparison.OrdinalIgnoreCase)) continue;
            var recursive = false;
            var force = false;
            for (var j = i + 1; j < tokens.Count; j++)
            {
                var token = tokens[j];
                if (token == "--") break;
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    if (token.Equals("--recursive", StringComparison.OrdinalIgnoreCase)) recursive = true;
                    else if (token.Equals("--force", StringComparison.OrdinalIgnoreCase)) force = true;
                    continue;
                }
                if (token.Length >= 2 && token[0] == '-' && token[1] != '-')
                {
                    for (var k = 1; k < token.Length; k++)
                    {
                        if (token[k] is 'r' or 'R') recursive = true;
                        if (token[k] == 'f') force = true;
                    }
                }
            }
            if (recursive && force) return true;
        }
        return false;
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
