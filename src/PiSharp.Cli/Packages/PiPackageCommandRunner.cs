using PiSharp.Compatibility.Settings;

namespace PiSharp.Cli.Packages;

public sealed class PiPackageCommandRunner : IPackageCommandRunner
{
    private readonly PiPackageSettingsService _settingsService;
    private readonly PiPackageManager _packageManager;
    private readonly NativeExtensionInstaller? _nativeExtensionInstaller;

    public PiPackageCommandRunner(PiPackageSettingsService settingsService, PiPackageManager packageManager)
        : this(settingsService, packageManager, null)
    {
    }

    public PiPackageCommandRunner(
        PiPackageSettingsService settingsService,
        PiPackageManager packageManager,
        NativeExtensionInstaller? nativeExtensionInstaller)
    {
        _settingsService = settingsService;
        _packageManager = packageManager;
        _nativeExtensionInstaller = nativeExtensionInstaller;
    }

    public async Task InstallAsync(string source, bool local, bool force = false, bool offline = false)
    {
        if (NativeExtensionInstaller.IsDllPath(source))
        {
            if (_nativeExtensionInstaller is null)
                throw new InvalidOperationException("Native extension installer is not configured.");

            await _nativeExtensionInstaller.InstallAsync(source, local, force);
            return;
        }

        var parsed = PiPackageSourceParser.Parse(source);

        switch (parsed.Kind)
        {
            case PiPackageSourceKind.Npm:
                await _packageManager.NpmInstallAsync(source, offline: offline, force: force);
                break;
            case PiPackageSourceKind.Git:
                await _packageManager.GitInstallAsync(source, offline: offline, force: force);
                break;
            case PiPackageSourceKind.Local:
                var exists = await _packageManager.LocalInstallAsync(parsed.LocalPath!);
                if (!exists) throw new InvalidOperationException($"Local package path not found: {parsed.LocalPath}");
                break;
        }

        await _settingsService.InstallAsync(source, local);
    }

    public async Task UpdateAsync(PackageUpdateRequest request)
    {
        if (request.Self)
        {
            throw new InvalidOperationException("Self-update is not yet implemented. Use your package manager to update PiSharp.");
        }

        if (request.Extensions || request.ExtensionSource is not null)
        {
            await UpdateExtensionsAsync(request);
            return;
        }

        if (request.Source is not null)
        {
            await UpdateSourceAsync(request);
            return;
        }

        await UpdateAllAsync(request);
    }

    private async Task UpdateExtensionsAsync(PackageUpdateRequest request)
    {
        var entries = await _settingsService.ListAsync();
        var targets = request.ExtensionSource is not null
            ? entries.Where(e => PiPackageSourceParser.Parse(e.Source).Identity == PiPackageSourceParser.Parse(request.ExtensionSource).Identity).ToList()
            : entries;

        var gitTasks = new List<Task>();
        foreach (var entry in targets)
        {
            var parsed = PiPackageSourceParser.Parse(entry.Source);
            switch (parsed.Kind)
            {
                case PiPackageSourceKind.Npm:
                    if (!request.Offline)
                        await _packageManager.NpmInstallAsync(entry.Source, offline: false, force: request.Force);
                    break;
                case PiPackageSourceKind.Git:
                    if (!request.Offline)
                        gitTasks.Add(_packageManager.GitUpdateAsync(entry.Source));
                    break;
            }
        }
        await Task.WhenAll(gitTasks);
    }

    private async Task UpdateSourceAsync(PackageUpdateRequest request)
    {
        var source = request.Source!;
        var parsed = PiPackageSourceParser.Parse(source);
        switch (parsed.Kind)
        {
            case PiPackageSourceKind.Npm:
                if (!request.Offline)
                    await _packageManager.NpmInstallAsync(source, offline: false, force: request.Force);
                break;
            case PiPackageSourceKind.Git:
                if (!request.Offline)
                {
                    var entries = await _settingsService.ListAsync();
                    var match = entries.FirstOrDefault(e =>
                        PiPackageSourceParser.Parse(e.Source).Identity == parsed.Identity);
                    if (match is not null)
                        await _packageManager.GitUpdateAsync(match.Source);
                }
                break;
        }
    }

    private async Task UpdateAllAsync(PackageUpdateRequest request)
    {
        var entries = await _settingsService.ListAsync();
        var gitTasks = new List<Task>();
        foreach (var entry in entries)
        {
            var parsed = PiPackageSourceParser.Parse(entry.Source);
            switch (parsed.Kind)
            {
                case PiPackageSourceKind.Npm:
                    if (!request.Offline)
                        await _packageManager.NpmInstallAsync(entry.Source, offline: false, force: request.Force);
                    break;
                case PiPackageSourceKind.Git:
                    if (!request.Offline)
                        gitTasks.Add(_packageManager.GitUpdateAsync(entry.Source));
                    break;
                case PiPackageSourceKind.Local:
                    break;
            }
        }
        await Task.WhenAll(gitTasks);
    }

    public async Task<bool> RemoveAsync(string source, bool local)
    {
        return await _settingsService.RemoveAsync(source, local);
    }

    public async Task<List<PackageListEntry>> ListAsync()
    {
        return await _settingsService.ListAsync();
    }

    public async Task ConfigAsync()
    {
        var entries = await ListAsync();
        if (entries.Count == 0)
        {
            Console.WriteLine("No packages installed.");
            return;
        }

        Console.WriteLine("Installed packages:");
        foreach (var entry in entries)
            Console.WriteLine($"  {entry.Source}  [{entry.Layer}]");
    }
}
