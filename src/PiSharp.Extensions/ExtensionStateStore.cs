using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Extensions;

/// <summary>
/// File-backed JSON key-value store for extension state, one per (namespace, scope).
/// Layout: <c>{ "schemaVersion": N, "data": { "&lt;key&gt;": &lt;json&gt; } }</c> at
/// <c>RootPath/state.json</c>, written atomically (temp file + <see cref="File.Move(string,string,bool)"/>)
/// under a per-store gate. Migrations registered before first load run in toVersion-ascending
/// order while the stored version matches a registered fromVersion.
/// </summary>
public sealed class ExtensionStateStore : IExtensionStateStore, IDisposable
{
    private sealed record StateMigration(
        int FromVersion,
        int ToVersion,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> Migrate);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<StateMigration> _migrations = [];
    private readonly ILogger _logger;
    private JsonObject? _data;
    private int _schemaVersion;
    private bool _loaded;

    public ExtensionStateStore(string extensionNamespace, ExtensionStateScope scope, string rootPath, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(extensionNamespace))
            throw new ArgumentException("Extension namespace is required.", nameof(extensionNamespace));
        Namespace = extensionNamespace;
        Scope = scope;
        RootPath = rootPath;
        FilePath = Path.Combine(rootPath, "state.json");
        _logger = loggerFactory?.CreateLogger<ExtensionStateStore>() ?? NullLogger<ExtensionStateStore>.Instance;
    }

    public string Namespace { get; }
    public ExtensionStateScope Scope { get; }
    public string RootPath { get; }
    public string FilePath { get; }

    public async Task<object?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ExtensionSettingKeys.ValidateStateKey(key);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _data!.TryGetPropertyValue(key, out var node) ? ExtensionSettingKeys.FromJsonNode(node) : null;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (value is null) return default;
        return JsonSerializer.Deserialize<T>(ExtensionSettingKeys.ToJsonNode(value)!.ToJsonString());
    }

    public async Task SetAsync(string key, object? value, CancellationToken cancellationToken = default)
    {
        ExtensionSettingKeys.ValidateStateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (value is null) _data!.Remove(key);
            else _data![key] = ExtensionSettingKeys.ToJsonNode(value);
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => await SetAsync(key, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, object?>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in _data!)
            result[pair.Key] = ExtensionSettingKeys.FromJsonNode(pair.Value);
        return result;
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _data!.Select(pair => pair.Key).ToArray();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            _data = [];
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _schemaVersion;
    }

    public async Task<int> SetSchemaVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version), "Schema version must be non-negative.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            _schemaVersion = version;
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            return _schemaVersion;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RegisterMigrationAsync(
        int fromVersion,
        int toVersion,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> migrate,
        CancellationToken cancellationToken = default)
    {
        if (migrate is null) throw new ArgumentNullException(nameof(migrate));
        if (fromVersion < 0) throw new ArgumentOutOfRangeException(nameof(fromVersion), "Migration fromVersion must be non-negative.");
        if (toVersion <= fromVersion) throw new ArgumentException("Migration toVersion must be greater than fromVersion.", nameof(toVersion));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _migrations.Add(new StateMigration(fromVersion, toVersion, migrate));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            await RunMigrationsCoreAsync(cancellationToken).ConfigureAwait(false);
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Load + migrate; caller must already hold <see cref="_gate"/>.</summary>
    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        await RunMigrationsCoreAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            _data = [];
            _schemaVersion = 0;
            return;
        }

        var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject ?? [];
        _schemaVersion = root["schemaVersion"] is JsonValue version && version.TryGetValue<int>(out var parsed) ? parsed : 0;
        _data = root["data"] as JsonObject ?? [];
    }

    private async Task RunMigrationsCoreAsync(CancellationToken cancellationToken)
    {
        var chain = _migrations.OrderBy(m => m.ToVersion).ToArray();
        if (chain.Length == 0) return;

        var applied = false;
        while (true)
        {
            var current = _schemaVersion;
            var migration = chain.FirstOrDefault(m => m.FromVersion == current);
            if (migration is null)
            {
                // A fresh store (no file, version 0) with migrations for a later baseline is
                // legitimate (the extension declares the baseline via SetSchemaVersionAsync);
                // a file already at the terminal toVersion of a registered chain is simply up
                // to date. Only an existing file stranded at a version no migration covers
                // (beside or above the chain) is a gap.
                if (_schemaVersion == 0 || chain.Any(m => m.ToVersion == _schemaVersion)) break;
                var registered = string.Join(", ", chain.Select(m => $"{m.FromVersion}->{m.ToVersion}"));
                throw new InvalidOperationException(
                    $"State store '{Namespace}' is at schemaVersion {_schemaVersion} but no registered migration covers that version (registered: {registered}).");
            }

            var input = ToObjectDictionary(_data!);
            IReadOnlyDictionary<string, object?> output;
            try
            {
                output = await migration.Migrate(input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "State migration {From}->{To} for namespace {Namespace} failed", migration.FromVersion, migration.ToVersion, Namespace);
                throw;
            }

            _data = ToJsonObject(output);
            _schemaVersion = migration.ToVersion;
            applied = true;
        }

        if (applied)
        {
            _logger.LogInformation("State store {Namespace} migrated to schemaVersion {Version}", Namespace, _schemaVersion);
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootPath);
        var root = new JsonObject
        {
            ["schemaVersion"] = _schemaVersion,
            ["data"] = _data?.DeepClone() ?? new JsonObject()
        };
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        var tempPath = Path.Combine(RootPath, $"state.json.tmp-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }

    private static IReadOnlyDictionary<string, object?> ToObjectDictionary(JsonObject data)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in data)
            result[pair.Key] = ExtensionSettingKeys.FromJsonNode(pair.Value);
        return result;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, object?> values)
    {
        var result = new JsonObject();
        foreach (var pair in values)
            result[pair.Key] = ExtensionSettingKeys.ToJsonNode(pair.Value);
        return result;
    }
}
