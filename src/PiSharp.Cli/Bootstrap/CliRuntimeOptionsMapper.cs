using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using PiSharp.Cli.Parsing;
using PiSharp.Compatibility.Settings;
using PiSharp.Runtime;

namespace PiSharp.Cli.Bootstrap;

public static class CliRuntimeOptionsMapper
{
    public static PiRuntimeOptions FromCliArgs(CliArgs args, PiSharp.Abstractions.Environment.IExecutionEnv env, HttpClient? httpClient = null, string? profile = null)
        => new(
            Env: env,
            Profile: profile,
            Model: new RuntimeModelOptions(args.Provider, args.Model, args.Thinking, args.Models),
            Tools: new RuntimeToolOptions(args.Tools, args.NoTools, args.NoBuiltinTools),
            Resources: new RuntimeResourceOptions(
                args.Extensions,
                args.Skills,
                args.PromptTemplates,
                args.Themes,
                args.NoResources || args.NoExtensions,
                args.NoTsExtensions,
                args.NoResources || args.NoSkills,
                args.NoResources || args.NoPromptTemplates,
                args.NoResources || args.NoThemes,
                args.NoResources || args.NoContextFiles),
            Prompt: new RuntimePromptOptions(args.SystemPrompt, args.AppendSystemPrompt),
            Extensions: new RuntimeExtensionOptions(args.UnknownFlagsOrEmpty),
            Session: new RuntimeSessionStartupOptions(
                NoSession: args.NoSession,
                SessionIdOrPath: string.IsNullOrWhiteSpace(args.Fork) ? args.Session : null,
                ContinueLatestForCwd: args.Continue,
                SessionDirectory: args.SessionDir,
                Fork: string.IsNullOrWhiteSpace(args.Fork) ? null : new RuntimeForkStartupOptions(args.Fork, args.Session)),
            HttpClient: httpClient,
            CompatibilityMode: args.CompatibilityMode,
            BenchmarkStartup: args.BenchmarkStartup,
            Verbose: args.Verbose);
}

internal static class StartupResumeSelector
{
    public static async Task<CliArgs?> ResolveAsync(
        CliArgs args,
        AppMode mode,
        IExecutionEnv env,
        Func<Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>, Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>, JsonlSessionMetadata?, CancellationToken, Task<JsonlSessionMetadata?>> picker,
        CancellationToken cancellationToken)
    {
        if (!args.Resume || mode != AppMode.Interactive) return args;

        var settings = await new PiSettingsStore().LoadAsync(env.Cwd, cancellationToken: cancellationToken);
        var sessionRoot = args.SessionDir ?? settings.Settings.SessionDir ?? settings.Paths.SessionsRoot;
        var repo = new JsonlSessionRepo(env, sessionRoot);
        JsonlSessionMetadata? firstCurrentSession = null;
        async Task<IReadOnlyList<JsonlSessionMetadata>> LoadCurrentAsync(CancellationToken token)
        {
            var sessions = await repo.ListAsync(new JsonlSessionListOptions(env.Cwd), token);
            firstCurrentSession ??= sessions.FirstOrDefault();
            return sessions;
        }
        var selected = await picker(
            LoadCurrentAsync,
            token => repo.ListAsync(null, token),
            firstCurrentSession,
            cancellationToken);
        return selected is null ? null : args with { Resume = false, Session = selected.Path };
    }
}
