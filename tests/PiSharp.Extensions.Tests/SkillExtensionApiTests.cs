using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class SkillExtensionApiTests
{
    [Fact]
    public async Task ExtensionApiCanRegisterSkills()
    {
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);

        await manager.InitializeAsync(
            new ExtensionDescriptor("skills", "Skills", "1.0.0"),
            new SkillExtension(),
            new ExtensionRuntimeActions("/repo", false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

        Assert.Collection(registry.Skills.OrderBy(skill => skill.Value.Name),
            skill =>
            {
                Assert.Equal("dynamic", skill.Value.Name);
                Assert.Equal("extension:skills", skill.SourceId);
            },
            skill =>
            {
                Assert.Equal("secondary", skill.Value.Name);
                Assert.Equal("extension:skills", skill.SourceId);
            });
    }

    [Fact]
    public async Task SkillApiReadsAndWritesRuntimeSelectedSkills()
    {
        var registry = new ExtensionRegistry();
        var selected = Array.Empty<string>();
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance)
        {
            GetAllSkillsAsync = _ => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>([
                new("alpha", "Alpha", "body", "/repo/alpha/SKILL.md")
            ]),
            GetSelectedSkillsAsync = _ => Task.FromResult<IReadOnlyList<string>>(selected),
            SetSelectedSkillsAsync = (names, _) => { selected = names.ToArray(); return Task.CompletedTask; }
        };
        var manager = new ExtensionManager(registry);

        await manager.InitializeAsync(new ExtensionDescriptor("reader", "Reader", "1.0.0"), new RuntimeSkillExtension(), binding);

        Assert.Equal(["alpha"], selected);
    }

    [Fact]
    public void UnregisterBySourceRemovesSkillRegistrations()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:skills", new ExtensionSkillRegistration("dynamic", "Dynamic skill", "body", "/repo/dynamic/SKILL.md"));

        var removed = registry.UnregisterBySource("extension:skills");

        Assert.Equal(1, removed);
        Assert.Empty(registry.Skills);
    }

    private sealed class SkillExtension : IExtension
    {
        public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
        {
            api.RegisterSkill(new ExtensionSkillRegistration("dynamic", "Dynamic skill", "body", "/repo/dynamic/SKILL.md"));
            api.Skills.RegisterSkill(new ExtensionSkillRegistration("secondary", "Secondary skill", "body", "/repo/secondary/SKILL.md"));
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeSkillExtension : IExtension
    {
        public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
        {
            var skills = await api.Skills.GetAllSkillsAsync(cancellationToken);
            await api.Skills.SetSelectedSkillsAsync(skills.Select(skill => skill.Name).ToArray(), cancellationToken);
        }
    }
}
