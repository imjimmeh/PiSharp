namespace PiSharp.Extensions;

/// <summary>Which state file a read/write targets: the user-global store or the project-local store.</summary>
public enum ExtensionStateScope { User, Project }

/// <summary>
/// File-backed, namespaced key-value store contract for extension-owned persistent state.
/// Values are JSON-serializable; <c>SetAsync(key, null)</c> removes the key. The store is
/// versioned via <c>schemaVersion</c> and may run registered migrations on first load.
/// </summary>
public interface IExtensionStateStore
{
    /// <summary>Normalized extension namespace this store serves (e.g. "pisharp-memory").</summary>
    string Namespace { get; }

    /// <summary>User (global) or Project (cwd-local) scope.</summary>
    ExtensionStateScope Scope { get; }

    /// <summary>Directory containing state.json.</summary>
    string RootPath { get; }

    Task<object?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, object? value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, object?>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Current schemaVersion (after any migrations registered so far have run).</summary>
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Declares the schemaVersion baseline explicitly; returns the stored version.</summary>
    Task<int> SetSchemaVersionAsync(int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a migration to run on first load when the file's schemaVersion equals
    /// <paramref name="fromVersion"/>. Migrations run in toVersion-ascending order while the
    /// current version matches a registered fromVersion; a gap raises an error. Function-form
    /// migrations are a native-only surface — TS extensions migrate manually.
    /// </summary>
    Task RegisterMigrationAsync(
        int fromVersion,
        int toVersion,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> migrate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-extension, namespaced state surface exposed on <see cref="IExtensionApi.State"/>.
/// Keys are flat strings owned by the extension; two extensions never share a store.
/// </summary>
public interface IExtensionStateApi
{
    Task<object?> GetAsync(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task<T?> GetAsync<T>(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task SetAsync(string key, object? value, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, object?>> GetAllAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListKeysAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task ClearAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);

    Task<int> GetSchemaVersionAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task<int> SetSchemaVersionAsync(int version, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default);
    Task RegisterMigrationAsync(
        int fromVersion,
        int toVersion,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> migrate,
        ExtensionStateScope scope = ExtensionStateScope.User,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Binding-facing factory implemented by the runtime: returns the file-backed store for a
/// normalized extension namespace and scope. Not exposed to extensions directly.
/// </summary>
public interface IExtensionRuntimeState
{
    IExtensionStateStore GetStore(string extensionNamespace, ExtensionStateScope scope);
}
