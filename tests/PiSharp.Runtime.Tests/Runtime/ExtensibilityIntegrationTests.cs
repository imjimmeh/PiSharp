using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

public sealed class ExtensibilityIntegrationTests
{
    [Fact]
    public async Task BootstrapActivatesPiSharpOverlayPromptTemplateThemeAndTsPromptExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-extensibility-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repo, "prompts"));
        Directory.CreateDirectory(Path.Combine(repo, "themes"));
        Directory.CreateDirectory(Path.Combine(repo, "extensions"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "PiSharp", "settings.json"), """
        {
          "pisharp": {
            "append": {
              "promptTemplates": ["./prompts"],
              "themes": ["./themes"],
              "extensions": ["./extensions/fixture.mjs"]
            }
          }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repo, "prompts", "release-notes.md"), "Release notes for $1\n$ARGUMENTS");
        await File.WriteAllTextAsync(Path.Combine(repo, "themes", "team.json"), "{\"name\":\"Dim Team\",\"tokens\":{\"accent\":\"#ffffff\"}}");
        await File.WriteAllTextAsync(Path.Combine(repo, "extensions", "fixture.mjs"), """
        export default function activate(pi) {
          pi.prompt.registerSection({ id: "team-rules", content: "Prefer team conventions." });
        }
        """);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repo),
            HomeDirectory: Path.Combine(root, "home"),
            Resources: new RuntimeResourceOptions(DisableSkills: true, DisableContextFiles: true)));

        Assert.Contains(runtime.PromptTemplates.Templates, template => template.Name == "release-notes");
        Assert.Equal("Release notes for 1.2.3\n1.2.3 stable", runtime.PromptTemplates.FormatInvocation("release-notes", ["1.2.3", "stable"]));
        Assert.Equal("Dim Team", runtime.Theme?.Name);
        Assert.Contains(runtime.ExtensionManager!.Registry.PromptSections, section => section.Value.Id == "team-rules");
    }
}
