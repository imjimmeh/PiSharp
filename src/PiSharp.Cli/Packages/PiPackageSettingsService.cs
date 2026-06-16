using PiSharp.Compatibility.Settings;

namespace PiSharp.Cli.Packages;

public sealed class PiPackageSettingsService
{
    private readonly PiSettingsStore _store;
    private PiSettingsSnapshot _snapshot;

    public PiPackageSettingsService(PiSettingsStore store, PiSettingsSnapshot snapshot)
    {
        _store = store;
        _snapshot = snapshot;
    }

    public async Task InstallAsync(string source, bool local = false)
    {
        if (local)
        {
            await _store.SaveProjectAsync(_snapshot, doc =>
            {
                var packages = doc.Settings.Packages.ToList();
                if (!packages.Contains(source, StringComparer.Ordinal))
                    packages.Add(source);
                doc.SetStringArray("packages", packages);
            });
        }
        else
        {
            await _store.SaveGlobalAsync(_snapshot, doc =>
            {
                var packages = doc.Settings.Packages.ToList();
                if (!packages.Contains(source, StringComparer.Ordinal))
                    packages.Add(source);
                doc.SetStringArray("packages", packages);
            });
        }

        _snapshot = await _store.LoadAsync(_snapshot.Paths.Cwd, _snapshot.Paths.HomeDirectory);
    }

    public async Task<bool> RemoveAsync(string source, bool local = false)
    {
        var sourceIdentity = PiPackageSourceParser.Parse(source).Identity;

        if (local)
        {
            var projectPkgs = _snapshot.Project.Settings.Packages.ToList();
            var removed = projectPkgs.RemoveAll(p =>
                string.Equals(PiPackageSourceParser.Parse(p).Identity, sourceIdentity, StringComparison.Ordinal));
            if (removed > 0)
            {
                await _store.SaveProjectAsync(_snapshot, doc =>
                    doc.SetStringArray("packages", projectPkgs));
                _snapshot = await _store.LoadAsync(_snapshot.Paths.Cwd, _snapshot.Paths.HomeDirectory);
                return true;
            }
            return false;
        }

        var globalPkgs = _snapshot.Global.Settings.Packages.ToList();
        var removedGlobal = globalPkgs.RemoveAll(p =>
            string.Equals(PiPackageSourceParser.Parse(p).Identity, sourceIdentity, StringComparison.Ordinal));

        if (removedGlobal > 0)
        {
            await _store.SaveGlobalAsync(_snapshot, doc =>
                doc.SetStringArray("packages", globalPkgs));
            _snapshot = await _store.LoadAsync(_snapshot.Paths.Cwd, _snapshot.Paths.HomeDirectory);
            return true;
        }

        var projectPkgs2 = _snapshot.Project.Settings.Packages.ToList();
        var removedProject = projectPkgs2.RemoveAll(p =>
            string.Equals(PiPackageSourceParser.Parse(p).Identity, sourceIdentity, StringComparison.Ordinal));
        if (removedProject > 0)
        {
            await _store.SaveProjectAsync(_snapshot, doc =>
                doc.SetStringArray("packages", projectPkgs2));
            _snapshot = await _store.LoadAsync(_snapshot.Paths.Cwd, _snapshot.Paths.HomeDirectory);
            return true;
        }

        return false;
    }

    public async Task<List<PackageListEntry>> ListAsync()
    {
        var identityMap = new Dictionary<string, PackageListEntry>(StringComparer.Ordinal);

        foreach (var pkg in _snapshot.Global.Settings.Packages)
        {
            var identity = PiPackageSourceParser.Parse(pkg).Identity;
            identityMap.TryAdd(identity, new PackageListEntry(pkg, PiSettingsLayer.GlobalLegacy));
        }

        foreach (var pkg in _snapshot.Project.Settings.Packages)
        {
            var identity = PiPackageSourceParser.Parse(pkg).Identity;
            identityMap[identity] = new PackageListEntry(pkg, PiSettingsLayer.ProjectLegacy);
        }

        return identityMap.Values.ToList();
    }
}
