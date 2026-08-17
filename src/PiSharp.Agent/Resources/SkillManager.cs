using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Environment;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using PiSharp.Extensions;
namespace PiSharp.Agent.Resources;

public sealed record Skill(
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false,
    IReadOnlyList<string>? Globs = null,
    bool AlwaysApply = false,
    bool Hide = false,
    string? Source = null,
    int SourcePriority = 0,
    ExtensionSkillRunner? Runner = null);
public sealed record SkillDiagnostic(string Type, string Code, string Message, string Path);

public static class SkillManager
{
    private static readonly string[] IgnoreNames = [".gitignore", ".ignore", ".fdignore"];

    public static async Task<(IReadOnlyList<Skill> Skills, IReadOnlyList<SkillDiagnostic> Diagnostics)> LoadAsync(IExecutionEnv env, string path, bool includeDirectMarkdownFiles = true, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger("PiSharp.Agent.Resources.SkillManager") ?? NullLogger.Instance;
        var skills = new List<Skill>();
        var diagnostics = new List<SkillDiagnostic>();
        var info = await env.GetFileInfoAsync(path, cancellationToken);
        if (info.IsErr)
        {
            diagnostics.Add(new("warning", "path_not_found", info.Error.Message, path));
            return (skills, diagnostics);
        }

        if (info.Value.Kind == FileKind.File)
        {
            var (skill, diagnostic) = await LoadSkillFileAsync(env, path, cancellationToken, logger);
            if (diagnostic is not null) diagnostics.Add(diagnostic);
            if (skill is not null) skills.Add(skill);
            return (skills, diagnostics);
        }

        await WalkDirectoryAsync(env, path, path, includeDirectMarkdownFiles, [], skills, diagnostics, cancellationToken, logger);
        return (skills, diagnostics);
    }

    public static string FormatInvocation(Skill skill, string? additionalInstructions = null)
    {
        var dir = Path.GetDirectoryName(skill.FilePath)?.Replace('\\', '/') ?? "/";
        var block = $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {dir}.\n\n{skill.Content}\n</skill>";
        return additionalInstructions is null ? block : $"{block}\n\n{additionalInstructions}";
    }

    private static async Task WalkDirectoryAsync(IExecutionEnv env, string dir, string rootDir, bool includeDirectMarkdownFiles, HashSet<string> ignoreMatcher, List<Skill> skills, List<SkillDiagnostic> diagnostics, CancellationToken cancellationToken, ILogger? logger = null)
    {
        await LoadIgnoreRulesAsync(env, ignoreMatcher, dir, rootDir, diagnostics, cancellationToken);
        var list = await env.ListDirectoryAsync(dir, cancellationToken);
        if (list.IsErr) { diagnostics.Add(new("warning", "list_failed", list.Error.Message, dir)); return; }
        var entries = list.Value.OrderBy(e => e.Name).ToArray();
        var directSkillFile = entries.FirstOrDefault(entry => entry.Kind == FileKind.File && string.Equals(entry.Name, "SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (directSkillFile is not null)
        {
            var (result, diagnostic) = await LoadSkillFileAsync(env, directSkillFile.Path, cancellationToken, logger);
            if (diagnostic is not null) diagnostics.Add(diagnostic);
            if (result is not null) skills.Add(result);
            return;
        }

        var fileLoadTasks = new List<Task<(Skill? Skill, SkillDiagnostic? Diagnostic)>>();
        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
            var rel = Path.GetRelativePath(rootDir, entry.Path).Replace('\\', '/');
            if (ignoreMatcher.Contains(rel) || ignoreMatcher.Contains(rel + "/")) continue;
            if (entry.Kind == FileKind.File)
            {
                if (!includeDirectMarkdownFiles || !entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                fileLoadTasks.Add(LoadSkillFileAsync(env, entry.Path, cancellationToken, logger));
                continue;
            }

            if (entry.Kind == FileKind.Directory) await WalkDirectoryAsync(env, entry.Path, rootDir, false, ignoreMatcher, skills, diagnostics, cancellationToken, logger);
        }

        var fileResults = await Task.WhenAll(fileLoadTasks);
        foreach (var (skill, diagnostic) in fileResults)
        {
            if (diagnostic is not null) diagnostics.Add(diagnostic);
            if (skill is not null) skills.Add(skill);
        }
    }

    private static async Task<(Skill? Skill, SkillDiagnostic? Diagnostic)> LoadSkillFileAsync(IExecutionEnv env, string path, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var read = await env.ReadTextFileAsync(path, cancellationToken);
        if (read.IsErr) return (null, new("warning", "read_failed", read.Error.Message, path));
        var (frontmatter, body) = ParseFrontmatter(read.Value, logger);
        var desc = frontmatter.TryGetValue("description", out var d) ? d?.ToString() : null;
        var name = frontmatter.TryGetValue("name", out var n) ? n?.ToString() : null;
        var fileName = Path.GetFileName(path);
        var dirName = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        name ??= string.Equals(fileName, "SKILL.md", StringComparison.OrdinalIgnoreCase) ? dirName : Path.GetFileNameWithoutExtension(path);
        var disableModel = frontmatter.TryGetValue("disable-model-invocation", out var dm) && IsTruthy(dm);
        if (string.IsNullOrWhiteSpace(desc))
            return (null, new("warning", "missing_description", "Skill frontmatter must include a description.", path));
        return (new Skill(name, desc, body, path, disableModel), null);
    }

    private static bool IsTruthy(object? value)
        => value switch
        {
            bool flag => flag,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static (Dictionary<string, object?> Frontmatter, string Body) ParseFrontmatter(string content, ILogger? logger = null)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---")) return ([], normalized);
        var endIndex = normalized.IndexOf("\n---", 3);
        if (endIndex == -1) return ([], normalized);
        try
        {
            var d = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
            return (d.Deserialize<Dictionary<string, object?>>(normalized[4..endIndex]) ?? [], normalized[(endIndex + 4)..].TrimStart());
        }
        catch (Exception ex) { logger?.LogDebug(ex, "Skill frontmatter parse failed"); return ([], normalized[(endIndex + 4)..].TrimStart()); }
    }

    private static async Task LoadIgnoreRulesAsync(IExecutionEnv env, HashSet<string> matcher, string dir, string rootDir, List<SkillDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var prefix = Path.GetRelativePath(rootDir, dir).Replace('\\', '/');
        if (prefix == ".") prefix = string.Empty;
        foreach (var name in IgnoreNames)
        {
            var p = $"{dir}/{name}";
            var read = await env.ReadTextFileAsync(p, cancellationToken);
            if (read.IsErr) continue;
            foreach (var raw in read.Value.Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || (line.StartsWith('#') && !line.StartsWith("\\#"))) continue;
                var pat = line.StartsWith('/') ? line[1..] : line;
                if (!string.IsNullOrEmpty(prefix)) pat = $"{prefix}/{pat}";
                matcher.Add(pat);
            }
        }
    }
}
