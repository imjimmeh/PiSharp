using System.Runtime.InteropServices;
using System.Text.Json;

namespace PiSharp.Coordination;

public enum FileActivityKind
{
    Read,
    Write
}

public sealed record FileToolActivity(FileActivityKind Kind, string FilePath);

public static class FileToolActivityParser
{
    private static readonly StringComparison RepoComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly HashSet<string> ReadToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read"
    };

    private static readonly HashSet<string> WriteToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "apply_patch"
    };

    private static readonly string[] PathArgumentNames = ["filePath", "path", "file"];

    public static FileToolActivity? Parse(string toolName, JsonElement arguments)
        => Parse(toolName, arguments, repoRoot: null);

    public static FileToolActivity? Parse(string toolName, JsonElement arguments, string? repoRoot)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;

        var kind = ResolveKind(toolName);
        if (kind is null)
            return null;

        foreach (var argName in PathArgumentNames)
        {
            if (arguments.TryGetProperty(argName, out var pathElement)
                && pathElement.ValueKind == JsonValueKind.String)
            {
                var path = pathElement.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    return new FileToolActivity(kind.Value, NormalizePath(path, repoRoot));
            }
        }

        return null;
    }

    internal static string NormalizePath(string rawPath, string? repoRoot)
    {
        var normalized = rawPath.Trim();

        if ((normalized.StartsWith(".\\") || normalized.StartsWith("./")) && normalized.Length > 2)
            normalized = normalized[2..];

        normalized = normalized.Replace('\\', '/');

        if (normalized.Length > 2 && !normalized.StartsWith("//"))
        {
            while (normalized.Contains("//"))
                normalized = normalized.Replace("//", "/");
        }
        else if (normalized.Length > 2)
        {
            var prefix = normalized[..2];
            var rest = normalized[2..];
            while (rest.Contains("//"))
                rest = rest.Replace("//", "/");
            normalized = prefix + rest;
        }

        if (repoRoot is not null)
        {
            var repoFull = Path.GetFullPath(repoRoot);
            if (repoFull.EndsWith('/') || repoFull.EndsWith('\\'))
                repoFull = repoFull[..^1];

            string resolved;
            if (Path.IsPathRooted(normalized))
                resolved = Path.GetFullPath(normalized);
            else
                resolved = Path.GetFullPath(Path.Combine(repoFull, normalized));

            var resolvedNormalized = resolved.Replace('\\', '/');

            var repoPrefix = repoFull.Replace('\\', '/') + "/";

            if (resolvedNormalized.StartsWith(repoPrefix, RepoComparison))
                normalized = resolvedNormalized[repoPrefix.Length..];
        }

        return normalized;
    }

    private static FileActivityKind? ResolveKind(string toolName)
    {
        if (ReadToolNames.Contains(toolName))
            return FileActivityKind.Read;

        if (WriteToolNames.Contains(toolName))
            return FileActivityKind.Write;

        return null;
    }
}
