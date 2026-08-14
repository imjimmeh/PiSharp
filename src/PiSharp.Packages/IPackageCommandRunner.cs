namespace PiSharp.Packages;

public interface IPackageCommandRunner
{
    Task InstallAsync(string source, bool local, bool force = false, bool offline = false);
    Task<bool> RemoveAsync(string source, bool local);
    Task<List<PackageListEntry>> ListAsync();
    Task ConfigAsync();
    Task UpdateAsync(PackageUpdateRequest request);
}
