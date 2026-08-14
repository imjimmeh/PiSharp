using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Extensions;

namespace PiSharp.Runtime;

/// <summary>
/// Isolated managed-skill store (GAP-56). Managed skills live at
/// <c>~/.pi/PiSharp/managed-skills</c> as <c>&lt;name&gt;/SKILL.md</c> packs with a
/// <c>managed-skills.json</c> index, are registered into the extension registry
/// with <c>Source="managed"</c>, <c>SourcePriority=5</c>, and emit
/// <c>skills_changed</c> on mutation. Restart load is idempotent.
/// </summary>
public sealed class ManagedSkillStore
{
    public const string ManagedSource = "managed";
    public const int ManagedSourcePriority = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _rootDirectory;
    private readonly string _indexPath;
    private readonly ExtensionRegistry? _registry;
    private readonly Func<string, object?, CancellationToken, Task> _emitEventAsync;
    private readonly ILogger _logger;

    public ManagedSkillStore(
        string rootDirectory,
        ExtensionRegistry? registry = null,
        Func<string, object?, CancellationToken, Task>? emitEventAsync = null,
        ILoggerFactory? loggerFactory = null)
    {
        _rootDirectory = rootDirectory;
        _indexPath = Path.Combine(rootDirectory, "managed-skills.json");
        _registry = registry;
        _emitEventAsync = emitEventAsync ?? ((_, _, _) => Task.CompletedTask);
        _logger = loggerFactory?.CreateLogger<ManagedSkillStore>() ?? NullLogger<ManagedSkillStore>.Instance;
    }

    public string RootDirectory => _rootDirectory;

    /// <summary>Loads the index and re-registers all managed skills into the registry (idempotent).</summary>
    public async Task<IReadOnlyList<ManagedSkillDescriptor>> LoadAsync(CancellationToken ct = default)
    {
        var descriptors = await ReadIndexAsync(ct);
        foreach (var descriptor in descriptors) Register(descriptor);
        return descriptors;
    }

    public async Task<ManagedSkillDescriptor> CreateAsync(ManagedSkillCreateRequest request, CancellationToken ct = default)
    {
        var name = NormalizeName(request.Name);
        if (name.Length == 0) throw new ArgumentException("Skill name is required.", nameof(request));
        var content = request.Content ?? string.Empty;
        var descriptor = new ManagedSkillDescriptor(name, request.Description, content, request.DisableModelInvocation, ManagedSource, ManagedSourcePriority);

        var descriptors = await ReadIndexAsync(ct);
        if (descriptors.Any(skill => StringComparer.Ordinal.Equals(skill.Name, name)))
            throw new InvalidOperationException($"A managed skill named '{name}' already exists.");

        await WritePackAsync(descriptor, ct);
        descriptors = [.. descriptors, descriptor];
        await WriteIndexAsync(descriptors, ct);
        Register(descriptor);
        await EmitChangedAsync(added: [name], ct: ct);
        return descriptor;
    }

    public async Task<ManagedSkillDescriptor> UpdateAsync(string name, ManagedSkillUpdateRequest request, CancellationToken ct = default)
    {
        name = NormalizeName(name);
        var descriptors = (await ReadIndexAsync(ct)).ToList();
        var index = descriptors.FindIndex(skill => StringComparer.Ordinal.Equals(skill.Name, name));
        if (index < 0) throw new InvalidOperationException($"Managed skill '{name}' was not found.");

        var current = descriptors[index];
        var updated = current with
        {
            Description = request.Description ?? current.Description,
            Content = request.Content ?? current.Content,
            DisableModelInvocation = request.DisableModelInvocation ?? current.DisableModelInvocation,
            Source = ManagedSource,
            SourcePriority = ManagedSourcePriority
        };

        await WritePackAsync(updated, ct);
        descriptors[index] = updated;
        await WriteIndexAsync(descriptors, ct);
        Register(updated);
        await EmitChangedAsync(updated: [name], ct: ct);
        return updated;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        name = NormalizeName(name);
        var descriptors = await ReadIndexAsync(ct);
        var removed = descriptors.Where(skill => StringComparer.Ordinal.Equals(skill.Name, name)).ToArray();
        if (removed.Length == 0) return false;

        await WriteIndexAsync(descriptors.Where(skill => !StringComparer.Ordinal.Equals(skill.Name, name)).ToArray(), ct);
        foreach (var descriptor in removed)
        {
            Unregister(descriptor);
            TryDeletePackDirectory(descriptor.Name);
        }
        await EmitChangedAsync(removed: [name], ct: ct);
        return true;
    }

    public async Task<IReadOnlyList<ManagedSkillDescriptor>> ListAsync(CancellationToken ct = default)
        => await ReadIndexAsync(ct);

    /// <summary>
    /// Promotes an existing skill (learn-to-skill, consumed by P08/P09) into the
    /// managed store. <paramref name="sourceReference"/> is a skill name or a
    /// <c>skill:&lt;name&gt;</c> reference resolved against the registry.
    /// </summary>
    public async Task<ManagedSkillDescriptor> PromoteAsync(string sourceReference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceReference)) throw new ArgumentException("Skill reference is required.", nameof(sourceReference));
        var name = sourceReference.StartsWith("skill:", StringComparison.OrdinalIgnoreCase)
            ? sourceReference[6..].Trim()
            : sourceReference.Trim();

        ExtensionSkillDefinition? source = null;
        if (_registry is not null)
        {
            source = _registry.Skills.FirstOrDefault(skill => StringComparer.Ordinal.Equals(skill.Value.Name, name))?.Value;
        }
        if (source is null) throw new InvalidOperationException($"Skill '{name}' was not found.");

        return await CreateAsync(new ManagedSkillCreateRequest(source.Name, source.Description, source.Content, source.DisableModelInvocation), ct);
    }

    private async Task<IReadOnlyList<ManagedSkillDescriptor>> ReadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexPath)) return [];
        try
        {
            await using var stream = File.OpenRead(_indexPath);
            var document = await JsonSerializer.DeserializeAsync<ManagedSkillIndexDocument>(stream, JsonOptions, ct);
            var descriptors = document?.Skills ?? [];
            return descriptors.Select(descriptor => descriptor with { Source = ManagedSource, SourcePriority = ManagedSourcePriority }).ToArray();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read managed-skill index at '{IndexPath}'; treating as empty.", _indexPath);
            return [];
        }
    }

    private async Task WriteIndexAsync(IReadOnlyList<ManagedSkillDescriptor> descriptors, CancellationToken ct)
    {
        Directory.CreateDirectory(_rootDirectory);
        var document = new ManagedSkillIndexDocument { Skills = descriptors.Select(d => d with { Source = ManagedSource, SourcePriority = ManagedSourcePriority }).ToList() };
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(_indexPath, json, ct);
    }

    private async Task WritePackAsync(ManagedSkillDescriptor descriptor, CancellationToken ct)
    {
        var packDirectory = Path.Combine(_rootDirectory, SafeDirectoryName(descriptor.Name));
        Directory.CreateDirectory(packDirectory);
        await File.WriteAllTextAsync(Path.Combine(packDirectory, "SKILL.md"), descriptor.Content, ct);
    }

    private void TryDeletePackDirectory(string name)
    {
        try
        {
            var packDirectory = Path.Combine(_rootDirectory, SafeDirectoryName(name));
            if (Directory.Exists(packDirectory)) Directory.Delete(packDirectory, recursive: true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete managed-skill pack directory for '{SkillName}'.", name);
        }
    }

    private void Register(ManagedSkillDescriptor descriptor)
    {
        if (_registry is null) return;
        var definition = new ExtensionSkillDefinition(
            descriptor.Name,
            descriptor.Description,
            descriptor.Content,
            Path.Combine(_rootDirectory, SafeDirectoryName(descriptor.Name), "SKILL.md"),
            descriptor.DisableModelInvocation,
            Globs: null,
            AlwaysApply: false,
            Hide: false,
            Source: ManagedSource,
            SourcePriority: ManagedSourcePriority);
        _registry.RegisterSkill(SourceId(descriptor.Name), definition, ExtensionOverridePolicy.Override);
    }

    private void Unregister(ManagedSkillDescriptor descriptor)
    {
        if (_registry is null) return;
        _registry.UnregisterBySource(SourceId(descriptor.Name));
    }

    private static string SourceId(string name) => ManagedSource + ":" + name;

    private Task EmitChangedAsync(IReadOnlyList<string>? added = null, IReadOnlyList<string>? removed = null, IReadOnlyList<string>? updated = null, CancellationToken ct = default)
        => _emitEventAsync(ExtensionEventNames.SkillsChanged, new
        {
            source = ManagedSource,
            added = added ?? [],
            removed = removed ?? [],
            updated = updated ?? []
        }, ct);

    private static string NormalizeName(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();

    private static string SafeDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return sanitized.Length == 0 ? "skill" : sanitized;
    }

    private sealed class ManagedSkillIndexDocument
    {
        public int Version { get; set; } = 1;
        public List<ManagedSkillDescriptor> Skills { get; set; } = [];
    }
}
