using System.Text.Json;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Runtime.Tests.Packages;

/// <summary>
/// P04 (GAP-56): the daemon-resident managed-skill store — isolated
/// <c>~/.pi/PiSharp/managed-skills</c> packs, <c>managed-skills.json</c> index,
/// registry registration with <c>Source="managed"</c>/<c>SourcePriority=5</c>,
/// <c>skills_changed</c> emission, and restart persistence.
/// </summary>
public sealed class ManagedSkillStoreTests
{
    [Fact]
    public async Task CreateWritesPackAndIndexRegistersSkillAndEmitsSkillsChanged()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var events = new List<(string Name, object? Payload)>();
        var store = new ManagedSkillStore(root, registry, (name, payload, _) => { events.Add((name, payload)); return Task.CompletedTask; });

        var descriptor = await store.CreateAsync(new ManagedSkillCreateRequest("learned", "Learned skill", "body", DisableModelInvocation: true));

        Assert.Equal("learned", descriptor.Name);
        Assert.Equal("Learned skill", descriptor.Description);
        Assert.Equal("body", descriptor.Content);
        Assert.True(descriptor.DisableModelInvocation);
        Assert.Equal("managed", descriptor.Source);
        Assert.Equal(5, descriptor.SourcePriority);

        Assert.Equal("body", await File.ReadAllTextAsync(Path.Combine(root, "learned", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(root, "managed-skills.json")));

        var registered = Assert.Single(registry.Skills).Value;
        Assert.Equal("learned", registered.Name);
        Assert.Equal("managed", registered.Source);
        Assert.Equal(5, registered.SourcePriority);
        Assert.EndsWith(Path.Combine("learned", "SKILL.md"), registered.FilePath, StringComparison.Ordinal);

        var (name, payload) = Assert.Single(events);
        Assert.Equal(ExtensionEventNames.SkillsChanged, name);
        Assert.Equal(["learned"], ReadList(payload, "added"));
    }

    [Fact]
    public async Task CreateDuplicateThrowsAndKeepsStoreIntact()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var store = new ManagedSkillStore(root, registry);
        await store.CreateAsync(new ManagedSkillCreateRequest("learned", "First", "body"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(new ManagedSkillCreateRequest("learned", "Second", "other")));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Single(await store.ListAsync());
        Assert.Single(registry.Skills);
    }

    [Fact]
    public async Task UpdateChangesContentAndDescriptionInStoreAndRegistry()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var events = new List<(string Name, object? Payload)>();
        var store = new ManagedSkillStore(root, registry, (name, payload, _) => { events.Add((name, payload)); return Task.CompletedTask; });
        await store.CreateAsync(new ManagedSkillCreateRequest("learned", "First", "body"));
        events.Clear();

        var updated = await store.UpdateAsync("learned", new ManagedSkillUpdateRequest(Description: "Second", Content: "new-body", DisableModelInvocation: true));

        Assert.Equal("Second", updated.Description);
        Assert.Equal("new-body", updated.Content);
        Assert.True(updated.DisableModelInvocation);
        Assert.Equal("new-body", await File.ReadAllTextAsync(Path.Combine(root, "learned", "SKILL.md")));

        var registered = Assert.Single(registry.Skills).Value;
        Assert.Equal("Second", registered.Description);
        Assert.Equal("new-body", registered.Content);
        Assert.True(registered.DisableModelInvocation);

        Assert.Equal(ExtensionEventNames.SkillsChanged, Assert.Single(events).Name);
        Assert.Equal(["learned"], ReadList(events[0].Payload, "updated"));
    }

    [Fact]
    public async Task UpdateMissingSkillThrows()
    {
        var root = TempRoot();
        var store = new ManagedSkillStore(root, new ExtensionRegistry());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync("missing", new ManagedSkillUpdateRequest(Content: "x")));
    }

    [Fact]
    public async Task DeleteRemovesPackIndexEntryAndRegistrySkill()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var events = new List<(string Name, object? Payload)>();
        var store = new ManagedSkillStore(root, registry, (name, payload, _) => { events.Add((name, payload)); return Task.CompletedTask; });
        await store.CreateAsync(new ManagedSkillCreateRequest("learned", "Learned", "body"));
        await store.CreateAsync(new ManagedSkillCreateRequest("kept", "Kept", "body"));
        events.Clear();
        Assert.Equal(2, registry.Skills.Count);

        var removed = await store.DeleteAsync("learned");

        Assert.True(removed);
        Assert.False(Directory.Exists(Path.Combine(root, "learned")));
        Assert.True(Directory.Exists(Path.Combine(root, "kept")));
        Assert.Equal(["kept"], (await store.ListAsync()).Select(skill => skill.Name));
        Assert.Equal("kept", Assert.Single(registry.Skills).Value.Name);

        Assert.Equal(ExtensionEventNames.SkillsChanged, Assert.Single(events).Name);
        Assert.Equal(["learned"], ReadList(events[0].Payload, "removed"));
    }

    [Fact]
    public async Task DeleteMissingSkillReturnsFalse()
    {
        var root = TempRoot();
        var store = new ManagedSkillStore(root, new ExtensionRegistry());

        Assert.False(await store.DeleteAsync("missing"));
    }

    [Fact]
    public async Task LoadAsyncIsIdempotentAcrossRestarts()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var firstStore = new ManagedSkillStore(root, registry);
        await firstStore.CreateAsync(new ManagedSkillCreateRequest("learned", "Learned", "body"));
        await firstStore.CreateAsync(new ManagedSkillCreateRequest("other", "Other", "content"));
        Assert.Equal(2, registry.Skills.Count);

        // Simulate restart: a fresh store + registry over the same root.
        var restartedRegistry = new ExtensionRegistry();
        var restartedStore = new ManagedSkillStore(root, restartedRegistry);
        var loaded = await restartedStore.LoadAsync();

        Assert.Equal(["learned", "other"], loaded.Select(skill => skill.Name).OrderBy(name => name));
        Assert.Equal(2, restartedRegistry.Skills.Count);
        Assert.All(restartedRegistry.Skills, registration =>
        {
            Assert.Equal("managed", registration.Value.Source);
            Assert.Equal(5, registration.Value.SourcePriority);
        });

        // Loading again must not duplicate registrations.
        await restartedStore.LoadAsync();
        Assert.Equal(2, restartedRegistry.Skills.Count);
    }

    [Fact]
    public async Task ListReturnsStoredDescriptors()
    {
        var root = TempRoot();
        var store = new ManagedSkillStore(root, new ExtensionRegistry());
        await store.CreateAsync(new ManagedSkillCreateRequest("alpha", "Alpha", "a"));

        var listed = await store.ListAsync();

        var descriptor = Assert.Single(listed);
        Assert.Equal("alpha", descriptor.Name);
        Assert.Equal("Alpha", descriptor.Description);
        Assert.Equal("a", descriptor.Content);
        Assert.Equal("managed", descriptor.Source);
        Assert.Equal(5, descriptor.SourcePriority);
    }

    [Fact]
    public async Task PromoteCopiesRegistrySkillIntoManagedStore()
    {
        var root = TempRoot();
        var registry = new ExtensionRegistry();
        var events = new List<(string Name, object? Payload)>();
        registry.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "source-skill", "Source", "source-body", "/repo/source/SKILL.md", DisableModelInvocation: true));
        var store = new ManagedSkillStore(root, registry, (name, payload, _) => { events.Add((name, payload)); return Task.CompletedTask; });

        var promoted = await store.PromoteAsync("skill:source-skill");

        Assert.Equal("source-skill", promoted.Name);
        Assert.Equal("Source", promoted.Description);
        Assert.Equal("source-body", promoted.Content);
        Assert.True(promoted.DisableModelInvocation);
        Assert.Equal("managed", promoted.Source);
        Assert.Equal(5, promoted.SourcePriority);
        Assert.Equal("source-body", await File.ReadAllTextAsync(Path.Combine(root, "source-skill", "SKILL.md")));
        Assert.Single(registry.Skills);
        Assert.Equal("managed", Assert.Single(registry.Skills).Value.Source);
        Assert.Equal(ExtensionEventNames.SkillsChanged, Assert.Single(events).Name);
    }

    [Fact]
    public async Task PromoteMissingSkillThrows()
    {
        var root = TempRoot();
        var store = new ManagedSkillStore(root, new ExtensionRegistry());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PromoteAsync("skill:missing"));
    }

    [Fact]
    public async Task ManagedSkillsSurviveRestartWithContentReadableFromIndex()
    {
        var root = TempRoot();
        var firstStore = new ManagedSkillStore(root, null);
        await firstStore.CreateAsync(new ManagedSkillCreateRequest("learned", "Learned", "persistent-body"));

        var restarted = new ManagedSkillStore(root, null);
        var descriptors = await restarted.LoadAsync();

        var descriptor = Assert.Single(descriptors);
        Assert.Equal("persistent-body", descriptor.Content);

        var document = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(root, "managed-skills.json")));
        Assert.Equal("learned", document.GetProperty("Skills")[0].GetProperty("Name").GetString());
    }

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "pisharp-managed-skills-" + Guid.NewGuid().ToString("N"));

    private static IReadOnlyList<string> ReadList(object? payload, string property)
    {
        using var document = JsonSerializer.SerializeToDocument(payload);
        return document.RootElement.GetProperty(property).EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }
}
