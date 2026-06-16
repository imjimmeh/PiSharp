using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Environment;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PiSharp.Agent.Resources;

public sealed record PromptTemplate(string Name, string? Description, string Content);
public sealed record PromptTemplateDiagnostic(string Type, string Code, string Message, string Path);

public static class PromptTemplateEngine
{
    private static ILogger _logger = NullLogger.Instance;

    public static void SetLogger(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger("PiSharp.Agent.Resources.PromptTemplateEngine") ?? NullLogger.Instance;
    }
    public static async Task<(IReadOnlyList<PromptTemplate> Templates, IReadOnlyList<PromptTemplateDiagnostic> Diagnostics)> LoadAsync(IExecutionEnv env, string path, CancellationToken cancellationToken = default)
    {
        var templates = new List<PromptTemplate>();
        var diagnostics = new List<PromptTemplateDiagnostic>();
        var list = await env.ListDirectoryAsync(path, cancellationToken);
        if (list.IsErr) { diagnostics.Add(new("warning", "list_failed", list.Error.Message, path)); return (templates, diagnostics); }
        foreach (var entry in list.Value.OrderBy(e => e.Name))
        {
            if (entry.Kind != FileKind.File || !entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            var read = await env.ReadTextFileAsync(entry.Path, cancellationToken);
            if (read.IsErr) { diagnostics.Add(new("warning", "read_failed", read.Error.Message, entry.Path)); continue; }
            var (frontmatter, body) = ParseFrontmatter(read.Value);
            var desc = frontmatter.TryGetValue("description", out var d) ? d?.ToString() : null;
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            templates.Add(new PromptTemplate(name, desc, body));
        }
        return (templates, diagnostics);
    }

    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        var result = Regex.Replace(content, @"\$(\d+)", m => int.TryParse(m.Groups[1].Value, out var i) && i > 0 && i <= args.Count ? args[i - 1] : "");
        result = Regex.Replace(result, @"\$\{@:(\d+)(?::(\d+))?\}", m =>
        {
            var start = Math.Max(0, int.Parse(m.Groups[1].Value) - 1);
            if (m.Groups[2].Success) return string.Join(" ", args.Skip(start).Take(int.Parse(m.Groups[2].Value)));
            return string.Join(" ", args.Skip(start));
        });
        var allArgs = string.Join(" ", args);
        return result.Replace("$ARGUMENTS", allArgs).Replace("$@", allArgs);
    }

    public static string FormatInvocation(PromptTemplate template, IReadOnlyList<string>? args = null) => SubstituteArgs(template.Content, args ?? []);

    private static (Dictionary<string, object?> Frontmatter, string Body) ParseFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---")) return ([], normalized);
        var end = normalized.IndexOf("\n---", 3);
        if (end == -1) return ([], normalized);
        try
        {
            var d = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
            return (d.Deserialize<Dictionary<string, object?>>(normalized[4..end]) ?? [], normalized[(end + 4)..].TrimStart());
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Template frontmatter parse failed"); return ([], normalized[(end + 4)..].TrimStart()); }
    }
}
