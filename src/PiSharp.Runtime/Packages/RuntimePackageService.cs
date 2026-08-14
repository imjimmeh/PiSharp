using PiSharp.Extensions;
using PiSharp.Packages;

namespace PiSharp.Runtime;

/// <summary>
/// Adapts the shared package engine (<see cref="IPackageCommandRunner"/>) to the
/// extension package API (<see cref="IExtensionPackageApi"/>). Successful
/// install/update/remove trigger a hot reload of extensions and emit
/// <c>extensions_changed</c> — runtime-side only; daemon dispatch lands in P01.
/// </summary>
public sealed class RuntimePackageService : IExtensionPackageApi, IDisposable
{
    private readonly Func<CancellationToken, Task<IPackageCommandRunner>> _runnerFactory;
    private readonly Func<CancellationToken, Task> _reloadExtensionsAsync;
    private readonly Func<string, object?, CancellationToken, Task> _emitEventAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPackageCommandRunner? _runner;

    public RuntimePackageService(
        Func<CancellationToken, Task<IPackageCommandRunner>> runnerFactory,
        Func<CancellationToken, Task> reloadExtensionsAsync,
        Func<string, object?, CancellationToken, Task> emitEventAsync)
    {
        _runnerFactory = runnerFactory;
        _reloadExtensionsAsync = reloadExtensionsAsync;
        _emitEventAsync = emitEventAsync;
    }

    public async Task<ExtensionPackageResult> InstallAsync(string reference, bool local = false, bool force = false, bool offline = false, CancellationToken ct = default)
    {
        try
        {
            var runner = await GetRunnerAsync(ct);
            await runner.InstallAsync(reference, local, force, offline);
            await ReloadAndNotifyAsync(added: [reference], ct: ct);
            return new ExtensionPackageResult(true);
        }
        catch (Exception exception)
        {
            return new ExtensionPackageResult(false, exception.Message);
        }
    }

    public async Task<ExtensionPackageResult> UpdateAsync(ExtensionPackageUpdateRequest request, CancellationToken ct = default)
    {
        try
        {
            var runner = await GetRunnerAsync(ct);
            await runner.UpdateAsync(new PackageUpdateRequest(
                Source: request.Source,
                Self: false,
                Extensions: request.Extensions,
                ExtensionSource: request.ExtensionSource,
                Force: request.Force,
                Offline: request.Offline));
            await ReloadAndNotifyAsync(updated: [request.Source ?? request.ExtensionSource ?? "*"], ct: ct);
            return new ExtensionPackageResult(true);
        }
        catch (Exception exception)
        {
            return new ExtensionPackageResult(false, exception.Message);
        }
    }

    public async Task<bool> RemoveAsync(string reference, bool local = false, CancellationToken ct = default)
    {
        try
        {
            var runner = await GetRunnerAsync(ct);
            var removed = await runner.RemoveAsync(reference, local);
            if (!removed) return false;
            await ReloadAndNotifyAsync(removed: [reference], ct: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ExtensionInstalledPackage>> ListAsync(CancellationToken ct = default)
    {
        var runner = await GetRunnerAsync(ct);
        var entries = await runner.ListAsync();
        return entries.Select(entry => new ExtensionInstalledPackage(entry.Source, entry.Layer.ToString())).ToArray();
    }

    private async Task ReloadAndNotifyAsync(IReadOnlyList<string>? added = null, IReadOnlyList<string>? removed = null, IReadOnlyList<string>? updated = null, CancellationToken ct = default)
    {
        await _reloadExtensionsAsync(ct);
        await _emitEventAsync(ExtensionEventNames.PackagesChanged, new
        {
            added = added ?? [],
            removed = removed ?? [],
            updated = updated ?? []
        }, ct);
    }

    private async Task<IPackageCommandRunner> GetRunnerAsync(CancellationToken ct)
    {
        if (_runner is not null) return _runner;
        await _gate.WaitAsync(ct);
        try
        {
            _runner ??= await _runnerFactory(ct);
            return _runner;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
