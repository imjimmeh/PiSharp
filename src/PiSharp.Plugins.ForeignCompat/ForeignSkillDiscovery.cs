using PiSharp.Extensions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// Shared foreign SKILL.md discovery (P11 plan §5.3): walks roots for
/// <c>SKILL.md</c>, parses YAML frontmatter (<c>name</c>/<c>description</c>/
/// <c>disable-model-invocation</c>), and maps to <see cref="ExtensionSkillDefinition"/>
/// with the provider's <c>Source</c>/<c>SourcePriority</c>. Semantics mirror the native
/// <c>SkillManager.ParseFrontmatter</c> (P11 plan §3): a missing <c>description</c>
/// drops the skill, and the skill name falls back to its directory name.
/// </summary>
public static class ForeignSkillDiscovery
{
    private static readonly YamlDotNet.Serialization.IDeserializer FrontmatterDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly string[] SkillFileNames = ["SKILL.md", "SKILL.MD"];

    /// <summary>
    /// Discovers <c>SKILL.md</c> files under each root (recursive), deduplicated by
    /// canonical path, frontmatter-parsed, glob-filtered, and converted to definitions.
    /// Roots should come from <see cref="ForeignPaths.DiscoverSkillDirs"/>.
    /// </summary>
    public static Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverFromRootsAsync(
        IReadOnlyList<string> roots,
        string source,
        int sourcePriority,
        ForeignCompatOptions options,
        CancellationToken cancellationToken = default)
    {
        var definitions = new List<ExtensionSkillDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var file in EnumerateSkillFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file);
                if (!seen.Add(full)) continue;

                var definition = TryLoadSkill(full, source, sourcePriority);
                if (definition is null) continue;
                if (!options.MatchesSkill(definition.Name, full)) continue;
                definitions.Add(definition);
            }
        }
        return Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>(definitions);
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in EnumerateFilesSafe(root))
        {
            var name = Path.GetFileName(file);
            if (SkillFileNames.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        Stack<string> pending = new();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(current);
                subdirectories = Directory.EnumerateDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var subdirectory in subdirectories) pending.Push(subdirectory);
        }
    }

    private static ExtensionSkillDefinition? TryLoadSkill(string path, string source, int sourcePriority)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (frontmatter, body) = ParseSkillFrontmatter(content);
        var description = frontmatter.TryGetValue("description", out var d) ? d?.ToString() : null;
        var name = frontmatter.TryGetValue("name", out var n) ? n?.ToString() : null;
        name ??= Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        var disableModelInvocation = frontmatter.TryGetValue("disable-model-invocation", out var dm) && IsTruthy(dm);

        if (string.IsNullOrWhiteSpace(description)) return null; // native semantics: description required
        return new ExtensionSkillDefinition(
            name,
            description,
            body,
            path,
            DisableModelInvocation: disableModelInvocation,
            Source: source,
            SourcePriority: sourcePriority);
    }

    private static bool IsTruthy(object? value)
        => value switch
        {
            bool flag => flag,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// Thin frontmatter reader mirroring <c>SkillManager.ParseFrontmatter</c> (private
    /// in core — P11 ships its own copy per plan §6; the optional core promotion was
    /// deliberately not adopted to keep P11 a pure plugin). Returns the parsed frontmatter
    /// map and the body after the closing <c>---</c>.
    /// </summary>
    public static (Dictionary<string, object?> Frontmatter, string Body) ParseSkillFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---")) return ([], normalized);
        var endIndex = normalized.IndexOf("\n---", 3);
        if (endIndex == -1) return ([], normalized);
        try
        {
            return (
                FrontmatterDeserializer.Deserialize<Dictionary<string, object?>>(normalized[4..endIndex]) ?? [],
                normalized[(endIndex + 4)..].TrimStart());
        }
        catch
        {
            return ([], normalized[(endIndex + 4)..].TrimStart());
        }
    }
}
