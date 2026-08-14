using Microsoft.Extensions.Logging;
using PiSharp.Ai.Auth;
using PiSharp.Cli.Bootstrap;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Files;
using PiSharp.Cli.IO;
using PiSharp.Cli.Logging;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Parsing;
using PiSharp.Compatibility.Settings;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using PiSharp.Tui.Interactive.Components;

namespace PiSharp.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
        => await RunAsync(args, new SystemConsoleIO(), cancellationToken: CancellationToken.None);

    public static async Task<int> RunAsync(string[] args, IConsoleIO console, IPackageCommandRunner? packageCommandRunner = null, CancellationToken cancellationToken = default)
    {
        var parsed = CliParser.Parse(args);
        foreach (var diagnostic in parsed.DiagnosticsOrEmpty)
        {
            await console.Error.WriteLineAsync($"{diagnostic.Type}: {diagnostic.Message}");
        }

        if (parsed.DiagnosticsOrEmpty.Any(d => d.Type == CliDiagnosticType.Error)) return 2;

        if (parsed.PackageCommand is not null)
        {
            var offline = parsed.Offline || string.Equals(Environment.GetEnvironmentVariable("PI_OFFLINE"), "1", StringComparison.Ordinal);
            return await HandlePackageCommandAsync(parsed.PackageCommand, console, packageCommandRunner, offline);
        }

        if (parsed.Version)
        {
            await console.Out.WriteLineAsync(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return 0;
        }

        if (parsed.DaemonCommand is not null)
        {
            return await DaemonMode.RunAsync(parsed.DaemonCommand, console, cancellationToken);
        }

        CliFileLoggingRegistration? fileLogging = null;
        var cwd = Directory.GetCurrentDirectory();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddDebug();
            fileLogging = CliFileLogging.AddConfiguredFileLogging(builder, cwd);
        });

        var mode = CliParser.SelectAppMode(parsed, console.IsInputRedirected);
        var env = new SystemExecutionEnv(cwd, loggerFactory);
        var runtimeArgs = parsed;
        if (parsed.Resume && !parsed.Help && mode == AppMode.Interactive)
        {
            var resolved = await StartupResumeSelector.ResolveAsync(
                parsed,
                mode,
                env,
                (loadCurrent, loadAll, current, token) => SessionSelectorDialog.SelectStandaloneAsync(loadCurrent, loadAll, current, token),
                cancellationToken);
            if (resolved is null) return 0;
            runtimeArgs = resolved;
        }

        var runtimeOptions = CliRuntimeOptionsMapper.FromCliArgs(
            runtimeArgs with { HelpOnly = parsed.Help },
            env);
        if (mode == AppMode.Interactive)
        {
            var extensionOptions = runtimeOptions.Extensions ?? new RuntimeExtensionOptions();
            runtimeOptions = runtimeOptions with
            {
                Extensions = extensionOptions with { DeferCachedActivationUntilUiReady = true }
            };
        }

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(
            runtimeOptions,
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);
        fileLogging?.SetSessionPath(runtime.Session.Metadata.Path);

        if (parsed.BenchmarkStartup && runtime.StartupBenchmark is not null)
        {
            await console.Error.WriteLineAsync(StartupBenchmarkFormatter.Render(runtime.StartupBenchmark));
        }

        foreach (var diagnostic in runtime.ExtensionFlagDiagnostics)
        {
            await console.Error.WriteLineAsync($"{diagnostic.Type}: {diagnostic.Message}");
        }

        if (runtime.ExtensionFlagDiagnostics.Any(d => d.Type == RuntimeDiagnosticType.Error)) return 2;
        if (parsed.Help)
        {
            await console.Out.WriteLineAsync(CliHelpRenderer.Render(new CliHelpOptions(runtime.ExtensionManager?.Registry.Flags.Select(flag => flag.Value).ToArray() ?? [])));
            return 0;
        }

        if (parsed.LoginProvider is not null || parsed.Logout)
        {
            return await HandleLoginLogoutAsync(parsed, env.Cwd, cancellationToken);
        }

        var fileReferences = parsed.FileArgsOrEmpty.Count == 0
            ? new ProcessedFileReferences(string.Empty, [])
            : await FileReferenceProcessor.ProcessFileArgumentsAsync(parsed.FileArgsOrEmpty, env.Cwd, cancellationToken);

        return mode switch
        {
            AppMode.Rpc => await RpcMode.RunAsync(runtime, console, cancellationToken),
            AppMode.SubagentJson => await SubagentJsonMode.RunAsync(runtime, new SubagentJsonModeOptions(InitialMessage: fileReferences.Text, Messages: parsed.MessagesOrEmpty), console, cancellationToken),
            AppMode.PrintJson => await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Json, InitialMessage: fileReferences.Text, Messages: parsed.MessagesOrEmpty, InitialImages: fileReferences.Images), console, cancellationToken),
            AppMode.PrintText => await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Text, InitialMessage: fileReferences.Text, Messages: parsed.MessagesOrEmpty, InitialImages: fileReferences.Images), console, cancellationToken),
            AppMode.Interactive => await InteractiveMode.RunAsync(runtime, cancellationToken),
            _ => 2
        };
    }

    private static async Task<int> HandlePackageCommandAsync(PackageCommandArgs cmd, IConsoleIO console, IPackageCommandRunner? runner, bool offline = false)
    {
        runner ??= await CreateDefaultPackageCommandRunnerAsync();

        return cmd.Kind switch
        {
            PackageCommandKind.Install => await HandleInstallAsync(cmd, console, runner, offline),
            PackageCommandKind.Remove => await HandleRemoveAsync(cmd, console, runner),
            PackageCommandKind.List => await HandleListAsync(console, runner),
            PackageCommandKind.Config => await HandleConfigAsync(console, runner),
            PackageCommandKind.Update => await HandleUpdateAsync(cmd, console, runner, offline),
            _ => 2
        };
    }

    private static async Task<IPackageCommandRunner> CreateDefaultPackageCommandRunnerAsync()
    {
        var cwd = Directory.GetCurrentDirectory();
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(cwd);
        var settingsService = new PiPackageSettingsService(store, snapshot);
        var processRunner = new SystemProcessRunner();
        var agentPaths = PiAgentPaths.FromCwd(cwd);
        var packageRoot = Path.Combine(agentPaths.GlobalAgentDirectory, "packages");
        var packageManager = new PiPackageManager(packageRoot, processRunner);
        var nativeExtensionInstaller = new NativeExtensionInstaller(snapshot.Paths.HomeDirectory, snapshot.Paths.Cwd);
        return new PiPackageCommandRunner(settingsService, packageManager, nativeExtensionInstaller);
    }

    private static async Task<int> HandleInstallAsync(PackageCommandArgs cmd, IConsoleIO console, IPackageCommandRunner runner, bool offline = false)
    {
        var source = cmd.Source!;
        await runner.InstallAsync(source, cmd.Local, cmd.Force, offline);
        await console.Out.WriteLineAsync($"Installed {source}");
        return 0;
    }

    private static async Task<int> HandleRemoveAsync(PackageCommandArgs cmd, IConsoleIO console, IPackageCommandRunner runner)
    {
        var source = cmd.Source!;
        var removed = await runner.RemoveAsync(source, cmd.Local);
        if (removed)
            await console.Out.WriteLineAsync($"Removed {source}");
        else
            await console.Out.WriteLineAsync($"No matching package found for {source}");
        return 0;
    }

    private static async Task<int> HandleListAsync(IConsoleIO console, IPackageCommandRunner runner)
    {
        var entries = await runner.ListAsync();
        if (entries.Count == 0)
        {
            await console.Out.WriteLineAsync("No packages installed.");
            return 0;
        }

        var userPackages = entries.Where(e => e.Layer is PiSettingsLayer.GlobalLegacy or PiSettingsLayer.GlobalPiSharp).ToList();
        var projectPackages = entries.Where(e => e.Layer is PiSettingsLayer.ProjectLegacy or PiSettingsLayer.ProjectPiSharp).ToList();

        if (userPackages.Count > 0)
        {
            await console.Out.WriteLineAsync("User packages:");
            foreach (var pkg in userPackages)
                await console.Out.WriteLineAsync($"  {pkg.Source}");
        }

        if (projectPackages.Count > 0)
        {
            await console.Out.WriteLineAsync("Project packages:");
            foreach (var pkg in projectPackages)
                await console.Out.WriteLineAsync($"  {pkg.Source}");
        }

        return 0;
    }

    private static async Task<int> HandleConfigAsync(IConsoleIO console, IPackageCommandRunner runner)
    {
        var entries = await runner.ListAsync();
        if (entries.Count == 0)
        {
            await console.Out.WriteLineAsync("No packages installed.");
            return 0;
        }

        var userPackages = entries.Where(e => e.Layer is PiSettingsLayer.GlobalLegacy or PiSettingsLayer.GlobalPiSharp).ToList();
        var projectPackages = entries.Where(e => e.Layer is PiSettingsLayer.ProjectLegacy or PiSettingsLayer.ProjectPiSharp).ToList();

        await console.Out.WriteLineAsync("Installed packages:");
        foreach (var pkg in userPackages)
            await console.Out.WriteLineAsync($"  {pkg.Source}  [user]");
        foreach (var pkg in projectPackages)
            await console.Out.WriteLineAsync($"  {pkg.Source}  [project]");

        return 0;
    }

    private static async Task<int> HandleUpdateAsync(PackageCommandArgs cmd, IConsoleIO console, IPackageCommandRunner runner, bool offline = false)
    {
        var request = new PackageUpdateRequest(
            cmd.Source, cmd.Self, cmd.Extensions, cmd.ExtensionSource, cmd.Force, offline);

        if (cmd.Self)
        {
            await console.Out.WriteLineAsync("Self-update is not yet implemented. Use your package manager to update PiSharp.");
            return 0;
        }

        await runner.UpdateAsync(request);
        await console.Out.WriteLineAsync("Update completed.");
        return 0;
    }

    private static async Task<int> HandleLoginLogoutAsync(CliArgs args, string cwd, CancellationToken cancellationToken = default)
    {
        var authPath = PiAgentPaths.FromCwd(cwd).AuthPath;
        var storage = new FileOAuthStorage(authPath);

        if (args.Logout)
        {
            var stored = await storage.ListStoredProvidersAsync();
            if (stored.Count == 0)
            {
                await Console.Out.WriteLineAsync("No stored credentials to remove.");
                return 0;
            }

            await Console.Out.WriteLineAsync("Providers with stored credentials:");
            for (var i = 0; i < stored.Count; i++)
                await Console.Out.WriteLineAsync($"  [{i + 1}] {stored[i]}");

            await Console.Out.WriteAsync("Enter number or provider name to log out (blank to cancel): ");
            var selection = (await Console.In.ReadLineAsync())?.Trim();
            if (string.IsNullOrWhiteSpace(selection))
            {
                await Console.Out.WriteLineAsync("Logout cancelled.");
                return 0;
            }

            string? selected = null;
            if (int.TryParse(selection, out var idx) && idx >= 1 && idx <= stored.Count)
                selected = stored[idx - 1];
            else
                selected = stored.FirstOrDefault(p => string.Equals(p, selection, StringComparison.OrdinalIgnoreCase));

            if (selected is null)
            {
                await Console.Out.WriteLineAsync("Invalid selection.");
                return 0;
            }

            await storage.RemoveTokenAsync(selected);
            await Console.Out.WriteLineAsync($"Removed stored credentials for {selected}.");
            return 0;
        }

        if (args.LoginProvider is not null)
        {
            var provider = args.LoginProvider == "default" ? null : args.LoginProvider;

            if (provider is null)
            {
                await Console.Out.WriteAsync("Enter provider name to log in: ");
                provider = (await Console.In.ReadLineAsync())?.Trim();
                if (string.IsNullOrWhiteSpace(provider))
                {
                    await Console.Out.WriteLineAsync("Login cancelled.");
                    return 0;
                }
            }

            if (OAuthProviderRegistry.IsOAuthProvider(provider))
            {
                var oauthProvider = OAuthProviderRegistry.Get(provider)!;
                await Console.Out.WriteLineAsync($"Starting {oauthProvider.Name} OAuth login...");

                var callbacks = new OAuthLoginCallbacks(
                    OnAuth: async authInfo =>
                    {
                        await OAuthBrowserLauncher.OpenAsync(authInfo.Url, cancellationToken);
                        await Console.Out.WriteLineAsync(authInfo.Url);
                        if (authInfo.Instructions is not null)
                            await Console.Out.WriteLineAsync(authInfo.Instructions);
                    },
                    OnPrompt: async (prompt, ct) =>
                    {
                        await Console.Out.WriteLineAsync(prompt.Message);
                        if (prompt.Placeholder is not null)
                            await Console.Out.WriteLineAsync($"({prompt.Placeholder})");
                        return (await Console.In.ReadLineAsync()) ?? string.Empty;
                    },
                    OnProgress: async msg => await Console.Out.WriteLineAsync(msg));

                try
                {
                    var credentials = await oauthProvider.LoginAsync(callbacks);
                    await storage.SetOAuthCredentialsAsync(provider, credentials);
                    await Console.Out.WriteLineAsync($"Successfully authenticated with {oauthProvider.Name}.");
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Login failed: {ex.Message}");
                    return 1;
                }
            }
            else
            {
                await Console.Out.WriteAsync($"Enter API key for {provider}: ");
                var apiKey = (await Console.In.ReadLineAsync())?.Trim();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    await Console.Out.WriteLineAsync("Login cancelled.");
                    return 0;
                }
                await storage.SetTokenAsync(provider, apiKey);
                await Console.Out.WriteLineAsync($"API key for {provider} saved.");
            }

            return 0;
        }

        return 0;
    }
}
