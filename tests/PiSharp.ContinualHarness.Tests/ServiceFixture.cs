using PiSharp.ContinualHarness;
using PiSharp.ContinualHarness.Contracts;

namespace PiSharp.ContinualHarness.Tests;

/// <summary>Builds a real service + per-scope stores over a temp journal, with a configurable target factory.</summary>
internal static class HarnessTestHost
{
    public sealed record Host(
        HarnessRefinementService Service,
        HarnessStore Local,
        HarnessStore Global,
        string LocalPath,
        string GlobalPath);

    public static Host Create(
        string tempDir,
        IHarnessSettings settings,
        Func<HarnessRefinementKind, HarnessRefinementScope, IRefinementTarget>? targetFactory = null,
        DateTimeOffset? fixedClock = null,
        StubEventBus? events = null,
        StubSessionApi? session = null)
    {
        var localPath = Path.Combine(tempDir, "refinements-local.jsonl");
        var globalPath = Path.Combine(tempDir, "refinements-global.jsonl");
        var local = new HarnessStore(HarnessRefinementScope.Local, localPath).Load();
        var global = new HarnessStore(HarnessRefinementScope.Global, globalPath).Load();

        var factory = targetFactory ?? ((kind, scope) => kind switch
        {
            HarnessRefinementKind.Prompt => new PromptSectionTarget(),
            HarnessRefinementKind.Subagent => new AgentDefinitionTarget(Path.Combine(tempDir, "agents")),
            _ => throw new HarnessRejectedException($"Unsupported kind '{kind}' in default test factory."),
        });

        var service = new HarnessRefinementService(
            local,
            global,
            factory,
            settings,
            emitEvent: events is null ? null : (name, payload, ct) => events.EmitAsync(name, payload!, ct),
            appendAudit: session is null ? null : (customType, payload, ct) => session.AppendEntryAsync(customType, payload!, ct),
            clock: () => fixedClock ?? DateTimeOffset.UtcNow);

        return new Host(service, local, global, localPath, globalPath);
    }
}
