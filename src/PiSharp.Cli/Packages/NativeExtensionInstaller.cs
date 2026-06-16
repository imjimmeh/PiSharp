namespace PiSharp.Cli.Packages;

public sealed class NativeExtensionInstaller
{
    private readonly string _homeDirectory;
    private readonly string _cwd;

    public NativeExtensionInstaller(string homeDirectory, string cwd)
    {
        _homeDirectory = homeDirectory;
        _cwd = cwd;
    }

    public async Task<string> InstallAsync(string sourcePath, bool local, bool force, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Native extension DLL not found: {sourcePath}", sourcePath);

        if (!IsDllPath(sourcePath))
            throw new InvalidOperationException($"Native extension install requires a .dll file: {sourcePath}");

        var destinationDirectory = local
            ? Path.Combine(_cwd, ".pi", "extensions")
            : Path.Combine(_homeDirectory, ".pi", "extensions");

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));

        if (File.Exists(destinationPath) && !force)
            throw new InvalidOperationException($"Native extension already exists: {destinationPath}. Use --force to replace it.");

        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = File.Open(destinationPath, force ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
        return destinationPath;
    }

    public static bool IsDllPath(string source)
        => string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase);
}
