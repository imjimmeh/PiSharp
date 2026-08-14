using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class PackageManagerTests
{
    [Fact]
    public async Task NpmInstallCreatesInstallRootPackageJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.NpmInstallAsync("npm:@foo/bar");

        var pkgJson = await File.ReadAllTextAsync(Path.Combine(packageRoot, "package.json"));
        Assert.Contains("private", pkgJson);
        Assert.Contains("true", pkgJson);
    }

    [Fact]
    public async Task NpmInstallInvokesNpmInstallArgs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.NpmInstallAsync("npm:@foo/bar");

        var cmd = Assert.Single(runner.Commands);
        Assert.Contains("npm", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@foo/bar", cmd);
    }

    [Fact]
    public async Task NpmUninstallInvokesUninstallArgs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.NpmUninstallAsync("npm:@foo/bar");

        var cmd = Assert.Single(runner.Commands);
        Assert.Contains("uninstall", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@foo/bar", cmd);
    }

    [Fact]
    public async Task GitInstallClonesIntoManagedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.GitInstallAsync("https://github.com/user/repo");

        var cmd = Assert.Single(runner.Commands);
        Assert.Contains("clone", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("github.com/user/repo", cmd);
    }

    [Fact]
    public async Task GitInstallRejectsCloneTargetOutsideManagedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GitInstallAsync("git:git@github.com:user/../../../../outside"));

        Assert.Contains("traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task GitInstallRejectsRepositoryPathTraversalInsideManagedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GitInstallAsync("git:git@github.com:../user/repo"));

        Assert.Contains("traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task LocalInstallChecksPathExistsAndDoesNotCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var localPath = Path.Combine(root, "my-pkg");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(localPath);
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        var result = await manager.LocalInstallAsync(localPath);

        Assert.True(result);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task LocalInstallReturnsFalseWhenPathDoesNotExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        var result = await manager.LocalInstallAsync(Path.Combine(root, "nonexistent"));

        Assert.False(result);
    }

    [Fact]
    public async Task OfflineModeSkipsNetworkRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.NpmInstallAsync("npm:@foo/bar", offline: true);

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task PinnedNpmSourceSkipsUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.NpmInstallAsync("npm:@foo/bar@1.2.3");

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task PinnedGitSourceSkipsUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.GitInstallAsync("https://github.com/user/repo#abc123");

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task GitUpdateDestructiveCommandsOnlyRunWhenTargetIsInsideManagedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        var outsidePath = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(outsidePath, ".git"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GitUpdateAsync("https://github.com/user/repo", outsidePath));

        Assert.Contains("managed root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitUpdateRunsInsideManagedCloneDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-mgr-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);

        await manager.GitUpdateAsync("git:https://github.com/user/repo");

        var cmd = Assert.Single(runner.Commands);
        Assert.Contains("git pull", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(packageRoot, "git", "github.com", "user", "repo"), Assert.Single(runner.WorkingDirectories));
    }
}

public sealed class NativeExtensionInstallerTests
{
    [Fact]
    public async Task NativeExtensionInstallerCopiesDllToGlobalExtensionsDirectory()
    {
        var fixture = await NativeExtensionInstallFixture.CreateAsync("dll-bytes");
        var installer = new NativeExtensionInstaller(fixture.Home, fixture.Cwd);

        var destination = await installer.InstallAsync(fixture.SourceDll, local: false, force: false);

        var expected = Path.Combine(fixture.Home, ".pi", "extensions", "Sample.Extension.dll");
        Assert.Equal(expected, destination);
        Assert.Equal("dll-bytes", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task NativeExtensionInstallerCopiesDllToLocalExtensionsDirectory()
    {
        var fixture = await NativeExtensionInstallFixture.CreateAsync("dll-bytes");
        var installer = new NativeExtensionInstaller(fixture.Home, fixture.Cwd);

        var destination = await installer.InstallAsync(fixture.SourceDll, local: true, force: false);

        var expected = Path.Combine(fixture.Cwd, ".pi", "extensions", "Sample.Extension.dll");
        Assert.Equal(expected, destination);
        Assert.Equal("dll-bytes", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task NativeExtensionInstallerRejectsOverwriteWithoutForce()
    {
        var fixture = await NativeExtensionInstallFixture.CreateAsync("new-bytes");
        var destination = Path.Combine(fixture.Home, ".pi", "extensions", "Sample.Extension.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old-bytes");
        var installer = new NativeExtensionInstaller(fixture.Home, fixture.Cwd);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(fixture.SourceDll, local: false, force: false));

        Assert.Contains("--force", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old-bytes", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task NativeExtensionInstallerOverwritesWithForce()
    {
        var fixture = await NativeExtensionInstallFixture.CreateAsync("new-bytes");
        var destination = Path.Combine(fixture.Home, ".pi", "extensions", "Sample.Extension.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old-bytes");
        var installer = new NativeExtensionInstaller(fixture.Home, fixture.Cwd);

        await installer.InstallAsync(fixture.SourceDll, local: false, force: true);

        Assert.Equal("new-bytes", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task NativeExtensionInstallerRejectsMissingDll()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-ext-" + Guid.NewGuid().ToString("N"));
        var installer = new NativeExtensionInstaller(Path.Combine(root, "home"), Path.Combine(root, "repo"));
        var missingDll = Path.Combine(root, "Missing.Extension.dll");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            installer.InstallAsync(missingDll, local: false, force: false));

        Assert.Equal(missingDll, ex.FileName);
    }

    [Fact]
    public async Task NativeExtensionInstallerRejectsNonDllFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Sample.txt");
        await File.WriteAllTextAsync(source, "not-a-dll");
        var installer = new NativeExtensionInstaller(Path.Combine(root, "home"), Path.Combine(root, "repo"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(source, local: false, force: false));

        Assert.Contains(".dll", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record NativeExtensionInstallFixture(string Home, string Cwd, string SourceDll)
    {
        public static async Task<NativeExtensionInstallFixture> CreateAsync(string content)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-native-ext-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var cwd = Path.Combine(root, "repo");
            Directory.CreateDirectory(cwd);
            var source = Path.Combine(root, "Sample.Extension.dll");
            await File.WriteAllTextAsync(source, content);
            return new NativeExtensionInstallFixture(home, cwd, source);
        }
    }
}

public sealed class PackageCommandRunnerNativeExtensionTests
{
    [Fact]
    public async Task RunnerInstallsDllGloballyWithoutPackageSettingsEntry()
    {
        var fixture = await PackageCommandRunnerFixture.CreateAsync("dll-bytes");

        await fixture.CommandRunner.InstallAsync(fixture.SourceDll, local: false);

        var expected = Path.Combine(fixture.Home, ".pi", "extensions", "Sample.Extension.dll");
        Assert.Equal("dll-bytes", await File.ReadAllTextAsync(expected));
        Assert.Empty(fixture.ProcessRunner.Commands);
        Assert.Empty(await fixture.SettingsService.ListAsync());
    }

    [Fact]
    public async Task RunnerInstallsDllLocallyWhenLocalFlagSet()
    {
        var fixture = await PackageCommandRunnerFixture.CreateAsync("dll-bytes");

        await fixture.CommandRunner.InstallAsync(fixture.SourceDll, local: true);

        var expected = Path.Combine(fixture.Repo, ".pi", "extensions", "Sample.Extension.dll");
        Assert.Equal("dll-bytes", await File.ReadAllTextAsync(expected));
        Assert.Empty(fixture.ProcessRunner.Commands);
        Assert.Empty(await fixture.SettingsService.ListAsync());
    }

    [Fact]
    public async Task RunnerKeepsNpmInstallBehavior()
    {
        var fixture = await PackageCommandRunnerFixture.CreateAsync("dll-bytes");

        await fixture.CommandRunner.InstallAsync("npm:@foo/bar", local: true, offline: true);

        Assert.Empty(fixture.ProcessRunner.Commands);
        var entry = Assert.Single(await fixture.SettingsService.ListAsync());
        Assert.Equal("npm:@foo/bar", entry.Source);
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".pi", "extensions")));
    }

    [Fact]
    public async Task RunnerKeepsLocalDirectoryPackageBehavior()
    {
        var fixture = await PackageCommandRunnerFixture.CreateAsync("dll-bytes");
        var localPackage = Path.Combine(fixture.Root, "local-package");
        Directory.CreateDirectory(localPackage);

        await fixture.CommandRunner.InstallAsync(localPackage, local: true);

        var entry = Assert.Single(await fixture.SettingsService.ListAsync());
        Assert.Equal(localPackage, entry.Source);
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".pi", "extensions")));
    }

    private sealed record PackageCommandRunnerFixture(
        string Root,
        string Home,
        string Repo,
        string SourceDll,
        FakeProcessRunner ProcessRunner,
        PiPackageSettingsService SettingsService,
        PiPackageCommandRunner CommandRunner)
    {
        public static async Task<PackageCommandRunnerFixture> CreateAsync(string dllContent)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-native-runner-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var repo = Path.Combine(root, "repo");
            var packagesDir = Path.Combine(root, "packages");
            Directory.CreateDirectory(repo);
            var sourceDll = Path.Combine(root, "Sample.Extension.dll");
            await File.WriteAllTextAsync(sourceDll, dllContent);

            var processRunner = new FakeProcessRunner();
            var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
            var snapshot = await store.LoadAsync(repo, home);
            var settingsService = new PiPackageSettingsService(store, snapshot);
            var manager = new PiPackageManager(packagesDir, processRunner);
            var nativeInstaller = new NativeExtensionInstaller(home, repo);
            var commandRunner = new PiPackageCommandRunner(settingsService, manager, nativeInstaller);

            return new PackageCommandRunnerFixture(root, home, repo, sourceDll, processRunner, settingsService, commandRunner);
        }
    }
}

public sealed class PackageManagerOfflineIntegrationTests
{
    [Fact]
    public async Task RunnerPassesOfflineToManagerWhichSkipsNpmInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-offline-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);
        var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
        var snapshot = await store.LoadAsync(root, root);
        var settingsService = new PiPackageSettingsService(store, snapshot);
        var commandRunner = new PiPackageCommandRunner(settingsService, manager);

        await commandRunner.InstallAsync("npm:@foo/bar", local: true, offline: true);

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task RunnerPassesOfflineToManagerWhichSkipsGitInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-git-offline-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var runner = new FakeProcessRunner();
        var manager = new PiPackageManager(packageRoot, runner);
        var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
        var snapshot = await store.LoadAsync(root, root);
        var settingsService = new PiPackageSettingsService(store, snapshot);
        var commandRunner = new PiPackageCommandRunner(settingsService, manager);

        await commandRunner.InstallAsync("git:https://github.com/user/repo", local: true, offline: true);

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task OfflineFlagViaProgramSkipsInstallNetworkCall()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-prog-offline-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "packages");
        var processRunner = new FakeProcessRunner();
        var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
        var snapshot = await store.LoadAsync(root, root);
        var settingsService = new PiPackageSettingsService(store, snapshot);
        var manager = new PiPackageManager(packageRoot, processRunner);
        var commandRunner = new PiPackageCommandRunner(settingsService, manager);
        var console = new TestConsoleIO();

        var exitCode = await Program.RunAsync(
            ["install", "npm:@foo/bar", "--local", "--offline"],
            console,
            packageCommandRunner: commandRunner);

        Assert.Equal(0, exitCode);
        Assert.Empty(processRunner.Commands);
    }
}

public sealed class PackageCommandRunnerUpdateTests
{
    [Fact]
    public async Task UpdateNoTargetInstallsAllConfiguredPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-upd-all-" + Guid.NewGuid().ToString("N"));
        var packagesDir = Path.Combine(root, "packages");
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);
        var processRunner = new FakeProcessRunner();
        var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        var settingsService = new PiPackageSettingsService(store, snapshot);

        await settingsService.InstallAsync("npm:@foo/bar");
        await settingsService.InstallAsync("npm:@baz/qux");

        var manager = new PiPackageManager(packagesDir, processRunner);
        var commandRunner = new PiPackageCommandRunner(settingsService, manager);

        await commandRunner.UpdateAsync(new PackageUpdateRequest());

        var installCalls = processRunner.Commands
            .Where(c => c.Contains("npm install", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(installCalls, c => c.Contains("@foo/bar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(installCalls, c => c.Contains("@baz/qux", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateOfflineSkipsNetworkCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-offline-upd-" + Guid.NewGuid().ToString("N"));
        var packagesDir = Path.Combine(root, "packages");
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);
        var processRunner = new FakeProcessRunner();
        var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        var settingsService = new PiPackageSettingsService(store, snapshot);

        await settingsService.InstallAsync("npm:@foo/bar");

        var manager = new PiPackageManager(packagesDir, processRunner);
        var commandRunner = new PiPackageCommandRunner(settingsService, manager);

        await commandRunner.UpdateAsync(new PackageUpdateRequest(Offline: true));

        Assert.Empty(processRunner.Commands);
    }

    [Fact]
    public async Task UpdatePinnedNpmSkipsUnlessForceIsSet()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-pinned-" + Guid.NewGuid().ToString("N"));
        var packagesDir = Path.Combine(root, "packages");
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);
        var processRunner = new FakeProcessRunner();
        var store = new PiSharp.Compatibility.Settings.PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        var settingsService = new PiPackageSettingsService(store, snapshot);

        await settingsService.InstallAsync("npm:@foo/bar@1.2.3");

        var manager = new PiPackageManager(packagesDir, processRunner);
        var commandRunner = new PiPackageCommandRunner(settingsService, manager);

        await commandRunner.UpdateAsync(new PackageUpdateRequest(Source: "npm:@foo/bar@1.2.3"));

        Assert.Empty(processRunner.Commands);

        await commandRunner.UpdateAsync(new PackageUpdateRequest(Source: "npm:@foo/bar@1.2.3", Force: true));

        Assert.NotEmpty(processRunner.Commands);
    }
}

public sealed class SystemProcessRunnerTests
{
    [Fact]
    public async Task ThrowsOnNonZeroExitCode()
    {
        var runner = new SystemProcessRunner();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("cmd", "/c exit 1"));

        Assert.Contains("code 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutableNotFoundThrowsDescriptiveError()
    {
        var runner = new SystemProcessRunner();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("nonexistent_tool_PiSharp_test_xyz", ""));

        Assert.Contains("nonexistent_tool_PiSharp_test_xyz", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("find", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunsProcessWithShellExecuteOnWindows()
    {
        var runner = new SystemProcessRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await runner.RunAsync("cmd", "/c echo hello", cancellationToken: cts.Token);
    }
}

public sealed class FakeProcessRunner : IPackageProcessRunner
{
    public List<string> Commands { get; } = [];
    public List<string?> WorkingDirectories { get; } = [];

    public Task RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        Commands.Add($"{fileName} {arguments}");
        WorkingDirectories.Add(workingDirectory);
        return Task.CompletedTask;
    }

    public Task<ProcessRunResult> RunCaptureAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        Commands.Add($"{fileName} {arguments}");
        WorkingDirectories.Add(workingDirectory);
        return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
    }
}
