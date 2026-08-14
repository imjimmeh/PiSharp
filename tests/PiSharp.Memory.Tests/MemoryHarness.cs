using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Memory.Abstractions;
using PiSharp.Memory.Backends.File;
using PiSharp.Memory.Backends.Off;
using PiSharp.Memory.Tests.Fakes;

namespace PiSharp.Memory.Tests;

/// <summary>
/// In-process harness: initializes the real <see cref="MemoryExtension"/> through
/// <see cref="ExtensionManager"/> with in-memory settings/state and captured
/// message/event sinks, registers fresh backend providers into the app-base
/// <see cref="MemoryServices"/> registry, and exposes registry/tool/event accessors.
/// </summary>
internal sealed class MemoryHarness : IAsyncDisposable
{
    public const string SettingsPrefix = "extensions.pisharp-memory.";

    private readonly List<CapturedSend> _sent = [];
    private readonly List<(string Name, object? Payload)> _emitted = [];

    private MemoryHarness(ExtensionRegistry registry, ExtensionManager manager, FakeRuntimeSettings settings, string cwd)
    {
        Registry = registry;
        Manager = manager;
        Settings = settings;
        Cwd = cwd;
    }

    public ExtensionRegistry Registry { get; }
    public ExtensionManager Manager { get; }
    public FakeRuntimeSettings Settings { get; }
    public MemoryExtension Extension { get; private set; } = null!;
    public string Cwd { get; }

    public IReadOnlyList<CapturedSend> SentMessages => _sent;
    public IReadOnlyList<(string Name, object? Payload)> EmittedEvents => _emitted;

    public static async Task<MemoryHarness> CreateAsync(
        string cwd = "C:/project",
        IReadOnlyDictionary<string, object?>? settings = null,
        string? fileRoot = null,
        bool withFileBackend = true)
    {
        var registry = new ExtensionRegistry { BuiltInToolNames = new HashSet<string>(StringComparer.Ordinal) };
        var manager = new ExtensionManager(registry);
        var fakeSettings = new FakeRuntimeSettings();
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
                await fakeSettings.SetRawAsync(SettingsPrefix + key, value, ExtensionSettingsScope.Source);
        }

        // Register fresh backend providers so every test is isolated from the app-base static registry.
        MemoryServices.Providers.Register(new OffMemoryProvider());
        if (withFileBackend)
        {
            var root = fileRoot ?? Path.Combine(Path.GetTempPath(), "pi-memory-tests", Guid.NewGuid().ToString("N"));
            MemoryServices.Providers.Register(new FileMemoryProvider(root, MemoryProjectKeys.Encode(cwd)));
        }

        var harness = new MemoryHarness(registry, manager, fakeSettings, cwd);
        var binding = new ExtensionRuntimeBinding(cwd, hasUi: false, NoExtensionUi.Instance)
        {
            RuntimeSettings = fakeSettings,
            SendMessageAsync = (message, delivery, trigger, ct) =>
            {
                harness._sent.Add(new CapturedSend(message, delivery, trigger, DateTimeOffset.UtcNow));
                return Task.CompletedTask;
            },
            EmitClientEventAsync = (name, payload, ct) =>
            {
                harness._emitted.Add((name, payload));
                return Task.CompletedTask;
            },
            GetSessionNameAsync = _ => Task.FromResult<string?>(null)
        };

        await manager.InitializeAsync(
            new ExtensionDescriptor("pisharp-memory", "PiSharp Memory", "1.0.0"),
            new MemoryExtension(),
            binding,
            CancellationToken.None);
        harness.Extension = (MemoryExtension)manager.Loaded[0].Instance;
        return harness;
    }

    public Task SetSettingAsync(string key, object? value)
        => Settings.SetRawAsync(SettingsPrefix + key, value, ExtensionSettingsScope.Source);

    public IAgentTool Tool(string name) => Registry.Tools.First(t => t.Value.Name == name).Value;

    public bool HasTool(string name) => Registry.Tools.Any(t => t.Value.Name == name);

    public IReadOnlyList<OwnedExtensionRegistration<ExtensionCommandRegistration>> Commands => Registry.Commands;

    public ExtensionCommandRegistration? FindCommand(string name)
        => Commands.Select(command => command.Value).FirstOrDefault(command => command.Name == name);

    public MentalModelPromptContributor? PromptContributor
        => Registry.PromptContributors.Select(contribution => contribution.Value).OfType<MentalModelPromptContributor>().FirstOrDefault();

    public async Task FireEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
    {
        var evt = new ExtensionEvent(eventName, null!, payload);
        foreach (var registration in Registry.HandlersFor(eventName))
            await registration.Value.Handler(evt, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Extension.DisposeAsync();
    }
}

internal sealed record CapturedSend(
    AgentMessage Message,
    ExtensionMessageDelivery Delivery,
    bool TriggerTurn,
    DateTimeOffset Timestamp);

internal static class MemoryTestHelpers
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(20);
        }
    }

    public static string TempDir()
        => Path.Combine(Path.GetTempPath(), "pi-memory-tests", Guid.NewGuid().ToString("N"));
}
