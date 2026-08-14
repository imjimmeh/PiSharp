namespace PiSharp.Extensions;

/// <summary>
/// Per-extension state surface: resolves the file-backed store for the extension's normalized
/// namespace and requested scope via the shared <see cref="IExtensionRuntimeState"/> factory.
/// Two extensions never share a store; keys are flat strings owned by the extension.
/// </summary>
public sealed class ExtensionScopedState : IExtensionStateApi
{
    private readonly IExtensionRuntimeState? _runtime;
    private readonly string _namespace;

    public ExtensionScopedState(ExtensionDescriptor descriptor, IExtensionRuntimeState? runtime)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        _runtime = runtime;
        _namespace = ExtensionNamespaces.Normalize(descriptor.Id);
    }

    public string Namespace => _namespace;

    public Task<object?> GetAsync(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        => StoreFor(scope) is { } store ? store.GetAsync(key, cancellationToken) : Task.FromResult<object?>(null);

    public Task<T?> GetAsync<T>(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        => StoreFor(scope) is { } store ? store.GetAsync<T>(key, cancellationToken) : Task.FromResult<T?>(default);

    public Task SetAsync(string key, object? value, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
    {
        var store = StoreFor(scope);
        if (store is null) throw new NotSupportedException("State is not available: the extension runtime has no state service bound.");
        return store.SetAsync(key, value, cancellationToken);
    }

    public Task RemoveAsync(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
    {
        var store = StoreFor(scope);
        if (store is null) throw new NotSupportedException("State is not available: the extension runtime has no state service bound.");
        return store.RemoveAsync(key, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, object?>> GetAllAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        => StoreFor(scope) is { } store ? store.GetAllAsync(cancellationToken) : Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());

    public Task<IReadOnlyList<string>> ListKeysAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        => StoreFor(scope) is { } store ? store.ListKeysAsync(cancellationToken) : Task.FromResult<IReadOnlyList<string>>([]);

    public Task ClearAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        => StoreFor(scope) is { } store ? store.ClearAsync(cancellationToken) : Task.CompletedTask;

    public Task<int> GetSchemaVersionAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        => StoreFor(scope) is { } store ? store.GetSchemaVersionAsync(cancellationToken) : Task.FromResult(0);

    public Task<int> SetSchemaVersionAsync(int version, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
    {
        var store = StoreFor(scope);
        if (store is null) throw new NotSupportedException("State is not available: the extension runtime has no state service bound.");
        return store.SetSchemaVersionAsync(version, cancellationToken);
    }

    public Task RegisterMigrationAsync(
        int fromVersion,
        int toVersion,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> migrate,
        ExtensionStateScope scope = ExtensionStateScope.User,
        CancellationToken cancellationToken = default)
    {
        var store = StoreFor(scope);
        if (store is null) throw new NotSupportedException("State is not available: the extension runtime has no state service bound.");
        return store.RegisterMigrationAsync(fromVersion, toVersion, migrate, cancellationToken);
    }

    private IExtensionStateStore? StoreFor(ExtensionStateScope scope)
        => _runtime?.GetStore(_namespace, scope);
}
