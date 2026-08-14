using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Auth;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;
using PiSharp.Tools;

[assembly: ExtensionMetadata("pisharp-git", Name = "PiSharp Git Integrations", Version = "0.1.0")]

namespace PiSharp.Git;

/// <summary>
/// Entry point for the git-integrations plugin. Registers the <c>commit</c> tool (sequential),
/// the <c>/commit</c> and <c>/share</c> slash commands, and emits git events.
/// </summary>
public sealed class GitExtension : IExtension, IAsyncDisposable
{
    private ShutdownHandle? _shutdown;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        var options = GitPluginOptions.FromEnvironment();
        var git = new GitRunner();
        var classifier = new ChangeClassifier(options);
        var inventoryService = new CommitInventoryService(git, classifier);
        var planner = new CommitPlanner();
        var graph = new CommitGraph();
        var executor = new CommitExecutor(git, graph);
        var tool = new CommitTool(inventoryService, executor, options) { Cwd = api.Cwd };

        // [optional: C2 IExtensionAuthApi touches src/PiSharp.Extensions (out of batch scope) —
        // fall back to constructing FileOAuthStorage directly from the agent auth path.]
        var authPath = PiAgentPaths.FromCwd(api.Cwd).AuthPath;
        var authStorage = new FileOAuthStorage(authPath);
        var tokenResolver = new GistTokenResolver(authStorage, options);
        // [optional: C4 GithubOAuthProvider touches src/PiSharp.Ai (out of batch scope) — users
        // store a PAT via env var or the auth store until then.]
        var uploader = new GitHubGistUploader(options);
        var host = CommandHostBuilder.FromApi(api);

        api.RegisterTool(new ExtensionToolRegistration(
            CommitTool.Name,
            CommitTool.Name,
            CommitTool.Description,
            ToolSchemas.FromType<CommitToolInput>(),
            (toolCallId, parameters, ct, _) => tool.ExecuteForHostAsync(toolCallId, parameters, ct),
            ExecutionMode: ToolExecutionMode.Sequential,
            PromptSnippet: "Create dependency-ordered atomic commits",
            PromptGuidelines:
            [
                "Call commit with no 'groups' first to get the authoritative change inventory.",
                "Partition EVERY change into exactly one group; a cycle is rejected.",
                "Lockfiles and generated files are excluded automatically and reported."
            ]));

        api.RegisterCommand(new ExtensionCommandRegistration(
            "commit",
            "Split unrelated working-tree changes into dependency-ordered atomic commits.",
            (args, ct) => new CommitSlashCommand(host, inventoryService, planner, executor, options).HandleAsync(args, ct)));

        var shareCommand = new ShareSlashCommand(host, uploader, tokenResolver, options);
        api.RegisterCommand(new ExtensionCommandRegistration(
            "share",
            "Upload a file (or the current session) as a GitHub gist, or copy it locally.",
            (args, ct) => shareCommand.HandleAsync(args, ct)));

        // Optional event emission (P25 observability consumers).
        executor.CommitCreated += e => _ = api.Events.EmitAsync(GitEventNames.CommitCreated, e, CancellationToken.None);
        shareCommand.ShareCompleted += e => _ = api.Events.EmitAsync(GitEventNames.ShareCompleted, e, CancellationToken.None);

        _shutdown = new ShutdownHandle(() => { });
    }
    public ValueTask DisposeAsync()
    {
        _shutdown?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class ShutdownHandle(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dispose();
            }
        }
    }
}
