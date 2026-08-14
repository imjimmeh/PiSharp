using System.Text;

namespace PiSharp.PlanMode;

public enum PlanFileStatus
{
    Draft,
    Approved,
    Aborted
}

/// <summary>
/// Parsed plan file: YAML frontmatter (<c>status</c>, <c>sessionId</c>, <c>model</c>,
/// <c>createdAt</c>, <c>updatedAt</c>) plus the markdown body.
/// </summary>
public sealed record PlanFileContents(
    PlanFileStatus Status,
    string SessionId,
    string? Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Body);

/// <summary>
/// Persists plan markdown files under <c>&lt;planFilesDir&gt;</c> named
/// <c>plan-&lt;sessionId8&gt;.md</c>. Writes are atomic (temp file + overwrite move);
/// status flips rewrite the file in place preserving the body.
/// </summary>
public sealed class PlanFileStore
{
    private const string FrontmatterStart = "---";
    private const string FrontmatterEnd = "---";

    private readonly string _plansDirectory;

    public PlanFileStore(string plansDirectory)
    {
        _plansDirectory = plansDirectory;
    }

    public string PlansDirectory => _plansDirectory;

    public static string ShortSessionId(string? sessionId)
    {
        var normalized = sessionId ?? string.Empty;
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    public string BuildPlanPath(string sessionId)
        => Path.Combine(_plansDirectory, $"plan-{ShortSessionId(sessionId)}.md");

    public async Task WriteDraftAsync(string path, string body, string sessionId, string? model, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await WriteAsync(path, new PlanFileContents(PlanFileStatus.Draft, sessionId, model, now, now, body), cancellationToken);
    }

    public async Task SetStatusAsync(string path, PlanFileStatus status, string sessionId, string? model, CancellationToken cancellationToken = default)
    {
        var existing = await ReadAsync(path, cancellationToken);
        var contents = new PlanFileContents(status, sessionId, model, existing.CreatedAt, DateTimeOffset.UtcNow, existing.Body);
        await WriteAsync(path, contents, cancellationToken);
    }

    public async Task<PlanFileContents> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var (frontmatter, body) = SplitFrontmatter(text);
        var fields = ParseFrontmatter(frontmatter);

        var status = fields.TryGetValue("status", out var statusValue)
            && Enum.TryParse<PlanFileStatus>(statusValue, ignoreCase: true, out var parsed)
            ? parsed
            : PlanFileStatus.Draft;

        return new PlanFileContents(
            status,
            fields.GetValueOrDefault("sessionId") ?? string.Empty,
            fields.GetValueOrDefault("model"),
            TryParseDate(fields.GetValueOrDefault("createdAt")),
            TryParseDate(fields.GetValueOrDefault("updatedAt")),
            body);
    }

    private async Task WriteAsync(string path, PlanFileContents contents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmpPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(tmpPath, Render(contents), Encoding.UTF8, cancellationToken);
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    private static string Render(PlanFileContents contents)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FrontmatterStart);
        builder.Append("status: ").AppendLine(contents.Status.ToString().ToLowerInvariant());
        builder.Append("sessionId: ").AppendLine(contents.SessionId);
        builder.Append("model: ").AppendLine(contents.Model ?? string.Empty);
        builder.Append("createdAt: ").AppendLine(contents.CreatedAt.ToString("O"));
        builder.Append("updatedAt: ").AppendLine(contents.UpdatedAt.ToString("O"));
        builder.AppendLine(FrontmatterEnd);
        builder.AppendLine();
        builder.AppendLine(contents.Body.TrimEnd('\r', '\n'));
        return builder.ToString();
    }

    private static (string Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length >= 3 && lines[0].TrimEnd('\r') == FrontmatterStart)
        {
            int end = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd('\r') == FrontmatterEnd)
                {
                    end = i;
                    break;
                }
            }
            if (end > 0)
            {
                var frontmatter = string.Join('\n', lines[1..end]);
                var body = string.Join('\n', lines[(end + 1)..]);
                return (frontmatter, body.TrimStart('\r', '\n'));
            }
        }
        return (string.Empty, text);
    }

    private static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return fields;
    }

    private static DateTimeOffset TryParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
}
