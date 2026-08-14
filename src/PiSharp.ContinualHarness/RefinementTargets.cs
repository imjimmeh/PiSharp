using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.ContinualHarness.Contracts;
using PiSharp.Extensions;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Internal seam that makes apply/rollback uniform and lets tests fake targets without touching the
/// service. Not on <see cref="IExtensionApi"/>; a future kind (e.g. rules, P10) can plug in by
public interface IRefinementTarget
{
    HarnessRefinementKind Kind { get; }
    Task<HarnessSyncedWith> ApplyCreateAsync(string name, JsonElement content, CancellationToken ct);
    Task<HarnessSyncedWith> ApplyUpdateAsync(string name, JsonElement content, CancellationToken ct);
    Task ApplyDeleteAsync(string name, JsonElement lastKnownContent, CancellationToken ct);
    Task<HarnessSyncedWith> ReadBackAsync(string name, CancellationToken ct);
    Task<string> DescribeAsync(string name, CancellationToken ct);
}

/// <summary>Content-hash helper shared by the file- and API-backed targets.</summary>
internal static class HarnessContentHash
{
    public static string OfText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string OfJson(JsonElement content)
        => OfText(content.GetRawText());
}

/// <summary>
/// Prompt-kind target is journal-only; there is no external file and nothing can be clobbered, so its
/// sync metadata is a self-reference. Shape: <c>{ "markdown": string, "slot": string, "priority": int }</c>.
/// </summary>
internal sealed class PromptSectionTarget : IRefinementTarget
{
    public HarnessRefinementKind Kind => HarnessRefinementKind.Prompt;

    public Task<HarnessSyncedWith> ApplyCreateAsync(string name, JsonElement content, CancellationToken ct)
    {
        ValidateContent(content);
        return Task.FromResult(Self());
    }

    public Task<HarnessSyncedWith> ApplyUpdateAsync(string name, JsonElement content, CancellationToken ct)
    {
        ValidateContent(content);
        return Task.FromResult(Self());
    }

    public Task ApplyDeleteAsync(string name, JsonElement lastKnownContent, CancellationToken ct)
        => Task.CompletedTask;

    public Task<HarnessSyncedWith> ReadBackAsync(string name, CancellationToken ct)
        => Task.FromResult(Self());

    public Task<string> DescribeAsync(string name, CancellationToken ct)
        => Task.FromResult("journal:prompt");

    private static HarnessSyncedWith Self()
        => new(Path: "journal:prompt", FileMtimeUtc: DateTimeOffset.UtcNow);

    private static void ValidateContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("markdown", out var markdown) || markdown.ValueKind != JsonValueKind.String)
            throw new HarnessRejectedException("Prompt content must be an object with a 'markdown' string.");
    }
}

/// <summary>
/// File-backed P06-format agent-definition target. Writes <c>.md</c> files (YAML frontmatter + body)
/// into a discovery root with mtime/hash clobber protection; creating a name that already exists is a
/// conflict (mirroring P06's first-wins tier precedence within the target root).
/// </summary>
internal sealed class AgentDefinitionTarget : IRefinementTarget
{
    private readonly string _rootDirectory;

    public AgentDefinitionTarget(string rootDirectory) => _rootDirectory = rootDirectory;

    public HarnessRefinementKind Kind => HarnessRefinementKind.Subagent;

    public Task<string> DescribeAsync(string name, CancellationToken ct)
        => Task.FromResult(FilePath(name));

    public Task<HarnessSyncedWith> ApplyCreateAsync(string name, JsonElement content, CancellationToken ct)
    {
        var markdown = ExtractMarkdown(content);
        var path = FilePath(name);
        if (File.Exists(path))
            throw new HarnessConflictException(
                KeyOf(name), path,
                expected: null, actual: Stat(path),
                diff: $"A definition named '{name}' already exists at {path}.");

        WriteFile(path, markdown);
        return Task.FromResult(Stat(path));
    }

    public Task<HarnessSyncedWith> ApplyUpdateAsync(string name, JsonElement content, CancellationToken ct)
    {
        var markdown = ExtractMarkdown(content);
        var path = FilePath(name);
        WriteFile(path, markdown);
        return Task.FromResult(Stat(path));
    }

    public Task ApplyDeleteAsync(string name, JsonElement lastKnownContent, CancellationToken ct)
    {
        var path = FilePath(name);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<HarnessSyncedWith> ReadBackAsync(string name, CancellationToken ct)
    {
        var path = FilePath(name);
        return Task.FromResult(File.Exists(path) ? Stat(path) : new HarnessSyncedWith(Path: path));
    }

    public string FilePath(string name) => Path.Combine(_rootDirectory, $"{name}.md");

    private static HarnessEntryKey KeyOf(string name) => new(HarnessRefinementKind.Subagent, name);

    private static string ExtractMarkdown(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("markdown", out var md))
            return md.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        throw new HarnessRejectedException("Subagent content must be an object with a 'markdown' string, or a raw string.");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static HarnessSyncedWith Stat(string path)
    {
        var mtime = new FileInfo(path).LastWriteTimeUtc;
        var hash = HarnessContentHash.OfText(File.ReadAllText(path));
        return new HarnessSyncedWith(Path: Path.GetFullPath(path), FileMtimeUtc: mtime, Sha256: hash);
    }
}

/// <summary>
/// Scaffolds a P06-convention agent-definition markdown file: a leading YAML frontmatter block
/// followed by the default system-prompt body. Values are single-line scalar quoted YAML.
/// </summary>
public sealed class AgentDefinitionWriter
{
    public string Write(string name, string description, string body, IReadOnlyList<string>? tools = null, string? model = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.Append("name: ").AppendLine(YamlQuote(name));
        sb.Append("description: ").AppendLine(YamlQuote(description));
        if (!string.IsNullOrWhiteSpace(model)) sb.Append("model: ").AppendLine(YamlQuote(model));
        if (tools is { Count: > 0 })
        {
            sb.AppendLine("tools:");
            foreach (var tool in tools) sb.Append("  - ").AppendLine(YamlQuote(tool));
        }
        sb.AppendLine("---");
        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.AppendLine();
            sb.Append(body);
            if (!body.EndsWith('\n')) sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string YamlQuote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var needsQuotes = value.Any(c => c is ':' or '#' or '"' or '\'' || char.IsWhiteSpace(c) || c is '\n' or '\t');
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }
}

/// <summary>
/// P04 managed-skill target over <see cref="IExtensionManagedSkillApi"/>. Global-only scope is
/// enforced by the service before the target is reached. Read-back compare uses the current API
/// content hash vs the journaled last-write.
/// </summary>
internal sealed class ManagedSkillTarget : IRefinementTarget
{
    private readonly IExtensionManagedSkillApi _api;

    public ManagedSkillTarget(IExtensionManagedSkillApi api) => _api = api;

    public HarnessRefinementKind Kind => HarnessRefinementKind.Skill;

    public async Task<HarnessSyncedWith> ApplyCreateAsync(string name, JsonElement content, CancellationToken ct)
    {
        var (description, body, disabled) = Parse(content);
        var existing = await FindAsync(name, ct);
        if (existing is not null)
            throw new HarnessConflictException(
                new HarnessEntryKey(HarnessRefinementKind.Skill, name),
                "api:ManagedSkill", null, existing.Value.SyncedWith,
                $"A managed skill named '{name}' already exists.");

        var created = await _api.CreateAsync(new ManagedSkillCreateRequest(name, description, body, disabled), ct);
        return SyncedFrom(created);
    }

    public async Task<HarnessSyncedWith> ApplyUpdateAsync(string name, JsonElement content, CancellationToken ct)
    {
        var (description, body, disabled) = Parse(content);
        var updated = await _api.UpdateAsync(name, new ManagedSkillUpdateRequest(description, body, disabled), ct);
        return SyncedFrom(updated);
    }

    public async Task ApplyDeleteAsync(string name, JsonElement lastKnownContent, CancellationToken ct)
        => await _api.DeleteAsync(name, ct);

    public async Task<HarnessSyncedWith> ReadBackAsync(string name, CancellationToken ct)
    {
        var existing = await FindAsync(name, ct);
        return existing?.SyncedWith ?? new HarnessSyncedWith(Path: "api:ManagedSkill", ApiUpdatedAt: DateTimeOffset.UtcNow);
    }

    public Task<string> DescribeAsync(string name, CancellationToken ct)
        => Task.FromResult("api:ManagedSkill");

    private async Task<(ManagedSkillDescriptor Descriptor, HarnessSyncedWith SyncedWith)?> FindAsync(string name, CancellationToken ct)
    {
        var all = await _api.ListAsync(ct);
        var found = all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (found is null) return null;
        return (found, SyncedFrom(found));
    }

    private static HarnessSyncedWith SyncedFrom(ManagedSkillDescriptor descriptor)
    {
        var contentJson = JsonSerializer.Serialize(new { descriptor.Description, descriptor.Content, descriptor.DisableModelInvocation });
        return new HarnessSyncedWith(Path: "api:ManagedSkill", ApiUpdatedAt: DateTimeOffset.UtcNow, Sha256: HarnessContentHash.OfText(contentJson));
    }

    private static (string Description, string Content, bool Disabled) Parse(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
            throw new HarnessRejectedException("Skill content must be an object with 'description' and 'content'.");
        var description = content.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
        var body = content.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var disabled = content.TryGetProperty("disableModelInvocation", out var dis) && dis.ValueKind == JsonValueKind.True;
        return (description, body, disabled);
    }
}

/// <summary>
/// P08 memory-kind target. Record keys are namespaced <c>refine/&lt;name&gt;</c>. Scope mapping
/// (Local -&gt; Project, Global -&gt; User) is decided by the service/target wiring. Read-back
/// compare covers a <c>learn</c>-tool or host write to the same memory record.
/// </summary>
internal sealed class MemoryTarget : IRefinementTarget
{
    private readonly IHarnessMemoryStore _store;

    public MemoryTarget(IHarnessMemoryStore store) => _store = store;

    public HarnessRefinementKind Kind => HarnessRefinementKind.Memory;

    public async Task<HarnessSyncedWith> ApplyCreateAsync(string name, JsonElement content, CancellationToken ct)
    {
        await _store.PutAsync(RecordKey(name), content, ct);
        return SyncedFrom(content);
    }

    public async Task<HarnessSyncedWith> ApplyUpdateAsync(string name, JsonElement content, CancellationToken ct)
    {
        await _store.PutAsync(RecordKey(name), content, ct);
        return SyncedFrom(content);
    }

    public async Task ApplyDeleteAsync(string name, JsonElement lastKnownContent, CancellationToken ct)
        => await _store.DeleteAsync(RecordKey(name), ct);

    public async Task<HarnessSyncedWith> ReadBackAsync(string name, CancellationToken ct)
    {
        var current = await _store.GetAsync(RecordKey(name), ct);
        return current is { } c
            ? new HarnessSyncedWith(Path: _store.Describe, ApiUpdatedAt: DateTimeOffset.UtcNow, Sha256: HarnessContentHash.OfJson(c))
            : new HarnessSyncedWith(Path: _store.Describe, ApiUpdatedAt: DateTimeOffset.UtcNow);
    }

    public Task<string> DescribeAsync(string name, CancellationToken ct)
        => Task.FromResult(_store.Describe);

    internal string RecordKey(string name) => "refine/" + name;

    private static HarnessSyncedWith SyncedFrom(JsonElement content)
        => new(Path: "api:Memory", ApiUpdatedAt: DateTimeOffset.UtcNow, Sha256: HarnessContentHash.OfJson(content));
}
