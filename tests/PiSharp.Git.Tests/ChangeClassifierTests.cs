using Xunit;

namespace PiSharp.Git.Tests;

public sealed class ChangeClassifierTests
{
    private static ChangeCategory C(string path) => new ChangeClassifier(new GitPluginOptions()).ClassifyPath(path);

    [Fact]
    public void SourceExtensionsClassifyAsSource()
    {
        Assert.Equal(ChangeCategory.Source, C("src/App.cs"));
        Assert.Equal(ChangeCategory.Source, C("src/foo.go"));
        Assert.Equal(ChangeCategory.Source, C("web/app.tsx"));
    }

    [Fact]
    public void TestPrecedenceOutranksSource()
    {
        Assert.Equal(ChangeCategory.Test, C("tests/App.Tests.cs"));
        Assert.Equal(ChangeCategory.Test, C("src/App.Tests.cs")); // marker wins even under src/
        Assert.Equal(ChangeCategory.Test, C("src/__tests__/Foo.ts"));
        Assert.Equal(ChangeCategory.Test, C("src/foo.spec.ts"));
    }

    [Fact]
    public void DocsAndConfigClassifyCorrectly()
    {
        Assert.Equal(ChangeCategory.Docs, C("docs/guide.md"));
        Assert.Equal(ChangeCategory.Docs, C("README.md"));
        Assert.Equal(ChangeCategory.Docs, C("docs/architecture/overview.rst"));
        Assert.Equal(ChangeCategory.Config, C("appsettings.json"));
        Assert.Equal(ChangeCategory.Config, C(".gitignore"));
        Assert.Equal(ChangeCategory.Config, C("Directory.Build.props"));
    }

    [Fact]
    public void OtherForUnrecognized()
    {
        Assert.Equal(ChangeCategory.Other, C("assets/logo.png"));
        Assert.Equal(ChangeCategory.Other, C("data.bin"));
    }

    [Fact]
    public void ScoresFollowTable()
    {
        Assert.Equal(4, ChangeClassifier.Score(ChangeCategory.Source));
        Assert.Equal(3, ChangeClassifier.Score(ChangeCategory.Test));
        Assert.Equal(2, ChangeClassifier.Score(ChangeCategory.Docs));
        Assert.Equal(1, ChangeClassifier.Score(ChangeCategory.Config));
        Assert.Equal(1, ChangeClassifier.Score(ChangeCategory.Other));
    }

    [Fact]
    public void DefaultExclusionsMatchLockfilesAndGenerated()
    {
        var classifier = new ChangeClassifier(new GitPluginOptions());
        var raw = new[]
        {
            new ChangeItem("src/App.cs", "M ", ChangeCategory.Other, 0, false, null),
            new ChangeItem("package-lock.json", "M ", ChangeCategory.Other, 0, false, null),
            new ChangeItem("src/obj/project.assets.json", "??", ChangeCategory.Other, 0, false, null),
        };

        var result = classifier.Classify(raw);

        Assert.Single(result.Changes);
        Assert.Equal("src/App.cs", result.Changes[0].Path);
        Assert.Equal(ChangeCategory.Source, result.Changes[0].Category);

        Assert.Contains("package-lock.json", result.ExcludedFiles);
        Assert.Contains("src/obj/project.assets.json", result.ExcludedFiles);
    }

    [Fact]
    public void ExtraExcludesAppendToDefaults()
    {
        var classifier = new ChangeClassifier(new GitPluginOptions());
        var raw = new[]
        {
            new ChangeItem("a.txt", "M ", ChangeCategory.Other, 0, false, null),
            new ChangeItem("b.generated", "M ", ChangeCategory.Other, 0, false, null),
        };

        var result = classifier.Classify(raw, extraExcludes: ["*.generated"]);

        Assert.Single(result.Changes);
        Assert.Contains("b.generated", result.ExcludedFiles);
    }

    [Fact]
    public void ScopeFilesRestrictsTheChangeSet()
    {
        var classifier = new ChangeClassifier(new GitPluginOptions());
        var raw = new[]
        {
            new ChangeItem("src/A.cs", "M ", ChangeCategory.Other, 0, false, null),
            new ChangeItem("tests/A.Tests.cs", "M ", ChangeCategory.Other, 0, false, null),
        };

        var result = classifier.Classify(raw, scopeFiles: ["tests/*"]);

        Assert.Single(result.Changes);
        Assert.Equal("tests/A.Tests.cs", result.Changes[0].Path);
    }

    [Fact]
    public void IncludeStagedFalseDropsStagedFilesButKeepsPureWorktree()
    {
        var classifier = new ChangeClassifier(new GitPluginOptions());
        var raw = new[]
        {
            new ChangeItem("staged.cs", "M ", ChangeCategory.Other, 0, false, null),  // X='M' staged, Y=' '
            new ChangeItem("mixed.cs", "MM", ChangeCategory.Other, 0, false, null),   // both
            new ChangeItem("worktree.cs", " M", ChangeCategory.Other, 0, false, null) // Y='M'
        };

        var result = classifier.Classify(raw, includeStaged: false, includeUnstaged: true);

        Assert.Single(result.Changes);
        Assert.Equal("worktree.cs", result.Changes[0].Path);
    }

    [Fact]
    public void IncludeUnstagedFalseDropsWorktreeAndUntracked()
    {
        var classifier = new ChangeClassifier(new GitPluginOptions());
        var raw = new[]
        {
            new ChangeItem("staged.cs", "M ", ChangeCategory.Other, 0, false, null),
            new ChangeItem("untracked.cs", "??", ChangeCategory.Other, 0, false, null),
            new ChangeItem("mixed.cs", "MM", ChangeCategory.Other, 0, false, null)
        };

        var result = classifier.Classify(raw, includeStaged: true, includeUnstaged: false);

        Assert.Single(result.Changes);
        Assert.Equal("staged.cs", result.Changes[0].Path);
    }

    [Fact]
    public void RenameDestinationClassifiesByDestination()
    {
        var classifier = new ChangeClassifier(new GitPluginOptions());
        var raw = new[]
        {
            new ChangeItem("src/New.cs", "R ", ChangeCategory.Other, 0, true, "src/Old.cs")
        };

        var result = classifier.Classify(raw);
        Assert.Equal(ChangeCategory.Source, Assert.Single(result.Changes).Category);
    }
}
