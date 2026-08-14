namespace PiSharp.Compatibility.Settings;

public sealed class PiSettingsStore
{
    public async Task<PiSettingsSnapshot> LoadAsync(string cwd, string? homeDirectory = null, string? profile = null, CancellationToken cancellationToken = default)
    {
        var paths = PiAgentPaths.FromCwd(cwd, homeDirectory, profile);
        var configuration = PiSettingsConfiguration.Build(paths);
        var global = await LoadDocumentAsync(paths.GlobalSettingsPath, cancellationToken);
        var globalPiSharp = await LoadDocumentAsync(paths.GlobalPiSharpSettingsPath, cancellationToken);
        var project = await LoadDocumentAsync(paths.ProjectSettingsPath, cancellationToken);
        var projectPiSharp = await LoadDocumentAsync(paths.ProjectPiSharpSettingsPath, cancellationToken);
        var merged = PiSettingsDocument.MergeMany([
            new PiSettingsLayerDocument(PiSettingsLayer.GlobalLegacy, global),
            new PiSettingsLayerDocument(PiSettingsLayer.GlobalPiSharp, globalPiSharp),
            new PiSettingsLayerDocument(PiSettingsLayer.ProjectLegacy, project),
            new PiSettingsLayerDocument(PiSettingsLayer.ProjectPiSharp, projectPiSharp)
        ], out var provenance);
        var resolvedSettings = PiSettings.FromConfiguration(configuration, merged.Settings);
        return new PiSettingsSnapshot(paths, global, project, merged, globalPiSharp, projectPiSharp, paths.Profile, provenance)
        {
            ResolvedSettings = resolvedSettings
        };
    }

    public async Task SaveGlobalAsync(PiSettingsSnapshot snapshot, Action<PiSettingsDocument> update, CancellationToken cancellationToken = default)
        => await SaveLayerAsync(snapshot, PiSettingsLayer.GlobalLegacy, update, cancellationToken);

    public async Task SaveProjectAsync(PiSettingsSnapshot snapshot, Action<PiSettingsDocument> update, CancellationToken cancellationToken = default)
        => await SaveLayerAsync(snapshot, PiSettingsLayer.ProjectLegacy, update, cancellationToken);

    public async Task SaveLayerAsync(PiSettingsSnapshot snapshot, PiSettingsLayer layer, Action<PiSettingsDocument> update, CancellationToken cancellationToken = default)
    {
        var (path, source) = layer switch
        {
            PiSettingsLayer.GlobalLegacy => (snapshot.Paths.GlobalSettingsPath, snapshot.Global),
            PiSettingsLayer.GlobalPiSharp => (snapshot.Paths.GlobalPiSharpSettingsPath, snapshot.GlobalPiSharpOrEmpty),
            PiSettingsLayer.ProjectLegacy => (snapshot.Paths.ProjectSettingsPath, snapshot.Project),
            PiSettingsLayer.ProjectPiSharp => (snapshot.Paths.ProjectPiSharpSettingsPath, snapshot.ProjectPiSharpOrEmpty),
            _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null)
        };
        var document = source.DeepClone();
        update(document);
        await SaveDocumentAsync(path, document, cancellationToken);
    }

    private static async Task<PiSettingsDocument> LoadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return PiSettingsDocument.Empty();
        return PiSettingsDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static async Task SaveDocumentAsync(string path, PiSettingsDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var json = document.ToJson();
        // Atomic replace: write a unique temp file in the same directory, then move over the
        // target so concurrent readers/writers never observe a partial settings file.
        var tempPath = Path.Combine(directory ?? string.Empty, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }
}
