using PiSharp.Cli;
using PiSharp.Compatibility.Resources;
using Xunit;

namespace PiSharp.Cli.Tests.Runtime;

public sealed class StartupResourceSummaryTests
{
    [Fact]
    public void CreateIncludesConfiguredResourceCategories()
    {
        var resources = new PiResources(
            [Path.Combine("repo", ".pi", "extensions", "redraws.ts")],
            [Path.Combine("repo", ".pi", "skills", "qa", "SKILL.md")],
            [Path.Combine("repo", ".pi", "prompts", "review.md")],
            [Path.Combine("repo", ".pi", "themes", "dark.json")],
            [],
            [],
            [new PiResolvedPackage("npm:@org/pkg", Path.Combine("home", ".pi", "agent", "packages", "pkg"), "cache")],
            []);

        var message = Assert.Single(StartupResourceSummary.Create(resources));

        Assert.Contains("Loaded extensions: redraws", message);
        Assert.Contains("Loaded skills: qa", message);
        Assert.Contains("Loaded prompt templates: review.md", message);
        Assert.Contains("Loaded themes: dark.json", message);
        Assert.Contains("Loaded packages: npm:@org/pkg", message);
    }

    [Fact]
    public async Task CreateExpandsSkillDirectoryToSkillNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-summary-" + Guid.NewGuid().ToString("N"));
        var skills = Directory.CreateDirectory(Path.Combine(root, "skills")).FullName;
        Directory.CreateDirectory(Path.Combine(skills, "qa"));
        await File.WriteAllTextAsync(Path.Combine(skills, "qa", "SKILL.md"), "---\nname: qa-session\ndescription: QA\n---\nbody\n");
        await File.WriteAllTextAsync(Path.Combine(skills, "review.md"), "---\nname: review-code\ndescription: Review\n---\nbody\n");
        var resources = new PiResources(
            [],
            [skills],
            [],
            [],
            [],
            [],
            [],
            []);

        var message = Assert.Single(StartupResourceSummary.Create(resources));

        Assert.Contains("Loaded skills: qa-session, review-code", message);
        Assert.DoesNotContain("Loaded skills: skills", message);
    }

    [Fact]
    public async Task CreateUsesPackageNameForExtensionResourcesInsidePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-summary-" + Guid.NewGuid().ToString("N"));
        var package = Directory.CreateDirectory(Path.Combine(root, "pkg")).FullName;
        var extension = Directory.CreateDirectory(Path.Combine(package, "dist")).FullName;
        await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"name\":\"pi-message-timestamps\"}\n");
        var resources = new PiResources(
            [extension],
            [],
            [],
            [],
            [],
            [],
            [new PiResolvedPackage(package, package, "local")],
            []);

        var message = Assert.Single(StartupResourceSummary.Create(resources));

        Assert.Contains("Loaded extensions: pi-message-timestamps", message);
    }

    [Fact]
    public void CreateOmitsEmptyCategoriesAndShowsWarningsWhenDiagnosticOnly()
    {
        var resources = new PiResources(
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [new PiResourceDiagnostic("extension", "missing", "missing", "/missing")]);

        var message = Assert.Single(StartupResourceSummary.Create(resources));

        Assert.DoesNotContain("Loaded extensions", message);
        Assert.DoesNotContain("Loaded skills", message);
        Assert.DoesNotContain("Loaded prompt templates", message);
        Assert.DoesNotContain("Loaded themes", message);
        Assert.DoesNotContain("Loaded packages", message);
        Assert.Contains("No packages or resources loaded.", message);
        Assert.Contains("Resource warnings: 1", message);
    }
}
