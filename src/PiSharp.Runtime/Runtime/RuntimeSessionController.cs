using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;

namespace PiSharp.Runtime;

internal sealed record RuntimeSessionReplacement(
    ISession<JsonlSessionMetadata> Session,
    AgentHarness<JsonlSessionMetadata> Harness,
    IDisposable? ExtensionDispatchSubscription);

internal sealed class RuntimeSessionController(
    ISessionRepo<JsonlSessionMetadata, JsonlSessionCreateOptions, JsonlSessionListOptions> repo,
    JsonlSessionCreateOptions createOptions,
    Func<ISession<JsonlSessionMetadata>, AgentHarness<JsonlSessionMetadata>> harnessFactory,
    ExtensionManager? extensionManager)
{
    public RuntimeExtensionBinder ExtensionBinder { get; } = new(extensionManager);
    public async Task<RuntimeSessionReplacement> NewSessionAsync(
        AgentHarness<JsonlSessionMetadata> currentHarness,
        CancellationToken cancellationToken = default)
        => await ReplaceSessionAsync(await repo.CreateAsync(createOptions, cancellationToken), currentHarness, cancellationToken);

    public async Task<RuntimeSessionReplacement> SwitchSessionAsync(
        JsonlSessionMetadata metadata,
        AgentHarness<JsonlSessionMetadata> currentHarness,
        CancellationToken cancellationToken = default)
        => await ReplaceSessionAsync(await repo.OpenAsync(metadata, cancellationToken), currentHarness, cancellationToken);

    public async Task<RuntimeSessionReplacement> ForkAsync(
        JsonlSessionMetadata source,
        SessionForkOptions forkOptions,
        AgentHarness<JsonlSessionMetadata> currentHarness,
        CancellationToken cancellationToken = default)
    {
        var forkCreateOptions = string.IsNullOrWhiteSpace(forkOptions.Id) ? createOptions : createOptions with { Id = forkOptions.Id };
        if (source.Id != currentHarness.Session.Metadata.Id) return await ReplaceSessionAsync(await repo.ForkAsync(source, forkCreateOptions, forkOptions, cancellationToken), currentHarness, cancellationToken);

        var entries = await SessionRepoUtils.GetEntriesToForkAsync(currentHarness.Session.Storage, forkOptions, cancellationToken);
        var fork = await repo.CreateAsync(forkCreateOptions with { ParentSessionPath = forkCreateOptions.ParentSessionPath ?? source.Path, PersistImmediately = true }, cancellationToken);
        foreach (var entry in entries) await fork.Storage.AppendEntryAsync(entry, cancellationToken);
        return await ReplaceSessionAsync(fork, currentHarness, cancellationToken);
    }

    public async Task<IReadOnlyList<JsonlSessionMetadata>> ListSessionsAsync(string? cwd = null, CancellationToken cancellationToken = default)
        => await repo.ListAsync(new JsonlSessionListOptions(cwd), cancellationToken);

    private async Task<RuntimeSessionReplacement> ReplaceSessionAsync(
        ISession<JsonlSessionMetadata> session,
        AgentHarness<JsonlSessionMetadata> currentHarness,
        CancellationToken cancellationToken)
    {
        var currentModel = currentHarness.Model;
        var currentThinking = currentHarness.ThinkingLevel;
        var harness = harnessFactory(session);
        await harness.SetModelAsync(currentModel, "runtime-rebind", cancellationToken);
        await harness.SetThinkingLevelAsync(currentThinking, cancellationToken);
        ExtensionBinder.ReplayTools(harness);
        ExtensionBinder.ReplaySkills(harness);
        harness.SetSelectedSkills(currentHarness.ExplicitSelectedSkillNames);
        var extensionDispatchSubscription = ExtensionBinder.BindHarnessDispatch(harness);
        return new RuntimeSessionReplacement(session, harness, extensionDispatchSubscription);
    }
}
