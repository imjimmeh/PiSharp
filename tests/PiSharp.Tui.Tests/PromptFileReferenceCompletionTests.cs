using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class PromptFileReferenceCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pisharp-at-complete-" + Guid.NewGuid().ToString("N"));

    public PromptFileReferenceCompletionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AtSignSuggestsFilesAndDirectoriesFromWorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "readme");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@", 1).Select(item => item.Value).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["@README.md", "@src/"], completions);
    }

    [Fact]
    public void BareAtSignSuggestsOnlyImmediateChildren()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "nested.txt"), "nested");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@src/", completions);
        Assert.DoesNotContain("@src/nested.txt", completions);
    }

    [Fact]
    public void SingleCharacterQuerySuggestsOnlyImmediateChildren()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), "export {};\n");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@i", "@i".Length).Select(item => item.Value).ToArray();

        Assert.DoesNotContain("@src/index.ts", completions);
    }

    [Fact]
    public void ScopedEmptyQuerySuggestsOnlyImmediateChildren()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        File.WriteAllText(Path.Combine(_root, "src", "nested", "deep.txt"), "deep");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@src/", "@src/".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/nested/", completions);
        Assert.DoesNotContain("@src/nested/deep.txt", completions);
    }

    [Fact]
    public void AtSignFuzzyMatchesNestedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), "export {};\n");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("Open @index", "Open @index".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/index.ts", completions);
    }

    [Fact]
    public void ScopedQueryDoesNotSuggestPathsAboveWorkingDirectory()
    {
        var sibling = _root + "-sibling";
        try
        {
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "outside.txt"), "outside");
            var provider = new PromptFileReferenceCompletionProvider(_root);

            var completions = provider.Complete("@../", "@../".Length).Select(item => item.Value).ToArray();

            Assert.Empty(completions);
        }
        finally
        {
            if (Directory.Exists(sibling)) Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void ScopedQueryNormalizesDisplayBaseWithinWorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), "export {};\n");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@src/../src/", "@src/../src/".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/index.ts", completions);
        Assert.DoesNotContain("@src/../src/index.ts", completions);
    }

    [Fact]
    public void AtSignQuotesPathsWithSpaces()
    {
        Directory.CreateDirectory(Path.Combine(_root, "my folder"));
        File.WriteAllText(Path.Combine(_root, "my folder", "test.txt"), "content");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@my", 3).Select(item => item.Value).ToArray();

        Assert.Contains("@\"my folder/\"", completions);
    }

    [Fact]
    public void PromptEditorAcceptsFileCompletionByReplacingOnlyAtToken()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), "export {};\n");
        var provider = new PromptFileReferenceCompletionProvider(_root);
        var prompt = new PromptEditor { Complete = (text, cursor) => provider.Complete(text, cursor) };
        prompt.SetPromptText("Please read @ind");

        prompt.AcceptFirstSuggestion();

        Assert.Equal("Please read @src/index.ts ", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorKeepsCursorInDirectoryCompletionWithoutAddingSpace()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var provider = new PromptFileReferenceCompletionProvider(_root);
        var prompt = new PromptEditor { Complete = (text, cursor) => provider.Complete(text, cursor) };
        prompt.SetPromptText("@sr");

        prompt.AcceptFirstSuggestion();

        Assert.Equal("@src/", prompt.PromptText);
    }

    [Fact]
    public async Task AtSignDoesNotSuggestGitIgnoredFiles()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.txt" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_root, "ignored.txt"), "secret content");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "public content");
        await RunGitAsync("init");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
        Assert.DoesNotContain("@ignored.txt", completions);
    }

    [Fact]
    public async Task GitIgnoredDirectoriesDoNotStarveVisibleMatches()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "zzz-ignored/" + Environment.NewLine);
        Directory.CreateDirectory(Path.Combine(_root, "kept"));
        File.WriteAllText(Path.Combine(_root, "kept", "target.txt"), "public content");
        Directory.CreateDirectory(Path.Combine(_root, "zzz-ignored"));
        for (var i = 0; i < 5010; i++)
        {
            File.WriteAllText(Path.Combine(_root, "zzz-ignored", $"noise-{i}.txt"), "noise");
        }
        await RunGitAsync("init");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@target", "@target".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@kept/target.txt", completions);
    }

    [Fact]
    public void CompletionStartsAfterWhitespaceBeforeAt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("read @ind", "read @ind".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/index.ts", completions);
    }

    [Fact]
    public void CompletionStartsForQuotedFileReferenceSyntax()
    {
        Directory.CreateDirectory(Path.Combine(_root, "my folder"));
        File.WriteAllText(Path.Combine(_root, "my folder", "test.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("read @\"my fo", "read @\"my fo".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@\"my folder/\"", completions);
    }

    [Fact]
    public void CompletionStartsWhenQuoteDelimitsTokenBeforeAt()
    {
        File.WriteAllText(Path.Combine(_root, "first.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completionsDouble = provider.Complete("\"@first", "\"@first".Length).Select(item => item.Value).ToArray();
        Assert.Contains("@first.txt", completionsDouble);

        var completionsSingle = provider.Complete("'@first", "'@first".Length).Select(item => item.Value).ToArray();
        Assert.Contains("@first.txt", completionsSingle);
    }

    [Fact]
    public void CompletionStartsAfterEqualsBeforeAt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("path=@ind", "path=@ind".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/index.ts", completions);
    }

    [Fact]
    public void NoCompletionWhenAtIsEmbeddedInsideWord()
    {
        File.WriteAllText(Path.Combine(_root, "world.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("hello@world", "hello@world".Length);

        Assert.Empty(completions);
    }

    [Fact]
    public void CursorOffsetLimitsTokenBeingCompleted()
    {
        File.WriteAllText(Path.Combine(_root, "first.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "second.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completionsAtFirst = provider.Complete("@first @second", 6).Select(item => item.Value).ToArray();
        Assert.Contains("@first.txt", completionsAtFirst);
        Assert.DoesNotContain("@second.txt", completionsAtFirst);

        var completionsAtSpace = provider.Complete("@first @second", 7);
        Assert.Empty(completionsAtSpace);
    }

    [Fact]
    public void ExactFilenameMatchOutranksContainsMatch()
    {
        File.WriteAllText(Path.Combine(_root, "ab.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "xab.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@ab.txt", "@ab.txt".Length).Select(item => item.Value).ToArray();

        Assert.Equal(["@ab.txt", "@xab.txt"], completions);
    }

    [Fact]
    public void ContainsMatchOutranksSubsequenceMatch()
    {
        File.WriteAllText(Path.Combine(_root, "abc.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "bac.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@bc", "@bc".Length).Select(item => item.Value).ToArray();

        Assert.Equal(["@abc.txt", "@bac.txt"], completions);
    }

    [Fact]
    public void DirectoryBonusPreserved_DirectoryOutranksFileWithEqualBaseScore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@src", "@src".Length).Select(item => item.Value).ToArray();

        Assert.Equal(["@src/", "@src.txt"], completions);
    }

    [Fact]
    public void NonMatchIsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "abc.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@xyz", "@xyz".Length);

        Assert.Empty(completions);
    }

    [Fact]
    public void EmptyQueryKeepsDirectoryBeforeFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), string.Empty);
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Equal(["@src/", "@readme.txt"], completions);
    }

    [Fact]
    public void BackslashQueryNormalizesConsistently()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        File.WriteAllText(Path.Combine(_root, "src", "nested", "deep.txt"), "content");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete(@"@src\nested\de", @"@src\nested\de".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/nested/deep.txt", completions);
        Assert.DoesNotContain("@src\\nested/deep.txt", completions);
    }

    [Fact]
    public void ScopedQueryPreservesExpectedCompletionPrefix()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "index.ts"), "export {};\n");
        var provider = new PromptFileReferenceCompletionProvider(_root);

        var completions = provider.Complete("@src/ind", "@src/ind".Length).Select(item => item.Value).ToArray();

        Assert.Contains("@src/index.ts", completions);
        Assert.DoesNotContain("@index.ts", completions);
    }

    [Fact]
    public void ProviderUsesGitVisibleEntriesWhenServiceReturnsEntries()
    {
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "content");
        File.WriteAllText(Path.Combine(_root, "hidden.txt"), "content");
        var fakeService = new FakeGitVisibilityService([Path.Combine(_root, "visible.txt")]);
        var provider = new PromptFileReferenceCompletionProvider(_root, fakeService);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
        Assert.DoesNotContain("@hidden.txt", completions);
    }

    [Fact]
    public void ProviderFallsBackToFilesystemWhenGitServiceReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "content");
        var fakeService = new FakeGitVisibilityService(Array.Empty<string>());
        var provider = new PromptFileReferenceCompletionProvider(_root, fakeService);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
    }

    [Fact]
    public void ProviderProfilingCountersTrackGitVisibilityPath()
    {
        var visiblePath = Path.Combine(_root, "visible.txt");
        var counters = new TuiProfilingCounters();
        var fakeService = new FakeGitVisibilityService([visiblePath]);
        var fakeFileSystem = new FakeFileSystem(
            new Dictionary<string, List<string>>
            {
                [_root] = [visiblePath]
            },
            new HashSet<string> { _root });
        var provider = new PromptFileReferenceCompletionProvider(
            _root,
            fakeService,
            fakeFileSystem,
            profilingCounters: counters);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
        Assert.Equal(1, counters.GetCount(TuiProfilingCounterNames.FileReferenceCompletion));
        Assert.Equal(1, counters.GetCount(TuiProfilingCounterNames.FileReferenceGitVisibility));
        Assert.True(counters.GetCount(TuiProfilingCounterNames.FileReferenceFileSystem) > 0);
    }

    [Fact]
    public void ProviderProfilingCountersTrackFilesystemFallbackPath()
    {
        var visiblePath = Path.Combine(_root, "visible.txt");
        var counters = new TuiProfilingCounters();
        var fakeService = new FakeGitVisibilityService(Array.Empty<string>());
        var fakeFileSystem = new FakeFileSystem(
            new Dictionary<string, List<string>>
            {
                [_root] = [visiblePath]
            },
            new HashSet<string> { _root });
        var provider = new PromptFileReferenceCompletionProvider(
            _root,
            fakeService,
            fakeFileSystem,
            profilingCounters: counters);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
        Assert.Equal(1, counters.GetCount(TuiProfilingCounterNames.FileReferenceCompletion));
        Assert.Equal(1, counters.GetCount(TuiProfilingCounterNames.FileReferenceGitVisibility));
        Assert.True(counters.GetCount(TuiProfilingCounterNames.FileReferenceFileSystem) > 0);
    }

    [Fact]
    public void ProviderFallsBackToFilesystemWhenGitServiceIsUnavailable()
    {
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "content");
        var provider = new PromptFileReferenceCompletionProvider(_root, gitVisibility: null);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
    }

    [Fact]
    public void GitDirectoryNotSuggested()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "config");
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "content");
        var provider = new PromptFileReferenceCompletionProvider(_root, gitVisibility: null);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@readme.txt", completions);
        Assert.DoesNotContain("@.git/", completions);
        Assert.DoesNotContain("@.git/config", completions);
    }

    [Fact]
    public void EnumerationErrorDoesNotFailCompletion()
    {
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "content");
        var failingDir = Path.Combine(_root, "failing");
        var fs = new FakeFileSystem(
            new Dictionary<string, List<string>>
            {
                [_root] = new() { failingDir, Path.Combine(_root, "visible.txt") },
                [failingDir] = new() { Path.Combine(failingDir, "hidden.txt") },
            },
            new HashSet<string> { _root, failingDir },
            failingDirectory: failingDir);
        var provider = new PromptFileReferenceCompletionProvider(_root, gitVisibility: null, fileSystem: fs);

        var completions = provider.Complete("@", 1).Select(item => item.Value).ToArray();

        Assert.Contains("@visible.txt", completions);
    }

    [Fact]
    public void ReusesEnumeratedEntriesForRepeatedRecursiveQueriesInSameScope()
    {
        var srcDir = Path.Combine(_root, "src");
        var docsDir = Path.Combine(_root, "docs");
        var fs = new FakeFileSystem(
            new Dictionary<string, List<string>>
            {
                [_root] = new() { srcDir, docsDir, Path.Combine(_root, "scratch.txt") },
                [srcDir] = new() { Path.Combine(srcDir, "server.cs"), Path.Combine(srcDir, "settings.json") },
                [docsDir] = new() { Path.Combine(docsDir, "readme.md") }
            },
            new HashSet<string> { _root, srcDir, docsDir });
        var provider = new PromptFileReferenceCompletionProvider(_root, gitVisibility: null, fileSystem: fs);

        var first = provider.Complete("@sr", 3).Select(item => item.Value).ToArray();
        var enumerationsAfterFirstQuery = fs.EnumerateFileSystemEntriesCallCount;
        var second = provider.Complete("@src", 4).Select(item => item.Value).ToArray();

        Assert.Contains("@src/", first);
        Assert.Contains("@src/", second);
        Assert.True(enumerationsAfterFirstQuery > 0);
        Assert.Equal(enumerationsAfterFirstQuery, fs.EnumerateFileSystemEntriesCallCount);
    }

    private sealed class FakeGitVisibilityService : IGitVisibilityService
    {
        private readonly string[] _paths;

        public FakeGitVisibilityService(string[] paths) => _paths = paths;

        public IEnumerable<string> EnumerateVisiblePaths(string baseDirectory, bool recursive) => _paths;
    }

    private async Task RunGitAsync(params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
    }
}

public sealed class FileReferenceEntryEnumeratorTests
{
    [Fact]
    public void FiltersOutGitDirectoryPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-enum-" + Guid.NewGuid().ToString("N"));
        var gitDir = Path.Combine(root, ".git");
        var srcDir = Path.Combine(root, "src");
        var directories = new HashSet<string> { root, gitDir, srcDir };
        var entries = new Dictionary<string, List<string>>
        {
            [root] = new() { gitDir, srcDir, Path.Combine(root, "readme.txt") },
            [gitDir] = new() { Path.Combine(gitDir, "config") },
            [srcDir] = new() { Path.Combine(srcDir, "index.txt") },
        };
        var fs = new FakeFileSystem(entries, directories);
        var enumerator = new FileReferenceEntryEnumerator(fs, NullLogger.Instance);

        var results = enumerator.EnumerateEntries(root, string.Empty, recursive: true)
            .Select(e => e.DisplayPath)
            .ToArray();

        Assert.Contains("readme.txt", results);
        Assert.Contains("src/index.txt", results);
        Assert.DoesNotContain(".git", results);
        Assert.DoesNotContain(".git/config", results);
    }

    [Fact]
    public void LimitsVisitedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-enum-" + Guid.NewGuid().ToString("N"));
        var directories = new HashSet<string> { root };
        var dirEntries = new List<string>();
        for (var i = 0; i < FileReferenceEntryEnumerator.MaxVisitedEntries + 100; i++)
            dirEntries.Add(Path.Combine(root, $"file-{i}.txt"));
        var entries = new Dictionary<string, List<string>> { [root] = dirEntries };
        var fs = new FakeFileSystem(entries, directories);
        var enumerator = new FileReferenceEntryEnumerator(fs, NullLogger.Instance);

        var results = enumerator.EnumerateEntries(root, string.Empty, recursive: false).ToArray();

        Assert.Equal(FileReferenceEntryEnumerator.MaxVisitedEntries, results.Length);
    }

    [Fact]
    public void SurvivesEnumerationErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-enum-" + Guid.NewGuid().ToString("N"));
        var failingDir = Path.Combine(root, "failing");
        var okDir = Path.Combine(root, "ok");
        var directories = new HashSet<string> { root, failingDir, okDir };
        var entries = new Dictionary<string, List<string>>
        {
            [root] = new() { failingDir, okDir },
            [failingDir] = new() { Path.Combine(failingDir, "hidden.txt") },
            [okDir] = new() { Path.Combine(okDir, "visible.txt") },
        };
        var fs = new FakeFileSystem(entries, directories, failingDirectory: failingDir);
        var enumerator = new FileReferenceEntryEnumerator(fs, NullLogger.Instance);

        var results = enumerator.EnumerateEntries(root, string.Empty, recursive: true)
            .Select(e => e.DisplayPath)
            .ToArray();

        Assert.Contains("ok/visible.txt", results);
    }

    [Fact]
    public void RespectsRecursiveFlag()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-enum-" + Guid.NewGuid().ToString("N"));
        var nestedDir = Path.Combine(root, "nested");
        var directories = new HashSet<string> { root, nestedDir };
        var entries = new Dictionary<string, List<string>>
        {
            [root] = new() { nestedDir, Path.Combine(root, "top.txt") },
            [nestedDir] = new() { Path.Combine(nestedDir, "deep.txt") },
        };
        var fs = new FakeFileSystem(entries, directories);
        var enumerator = new FileReferenceEntryEnumerator(fs, NullLogger.Instance);

        var nonRecursive = enumerator.EnumerateEntries(root, string.Empty, recursive: false)
            .Select(e => e.DisplayPath)
            .ToArray();

        Assert.Contains("top.txt", nonRecursive);
        Assert.Contains("nested", nonRecursive);
        Assert.DoesNotContain("nested/deep.txt", nonRecursive);

        var recursive = enumerator.EnumerateEntries(root, string.Empty, recursive: true)
            .Select(e => e.DisplayPath)
            .ToArray();

        Assert.Contains("nested/deep.txt", recursive);
    }

    [Fact]
    public void AppliesDisplayBaseToReturnedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-enum-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(root, "src");
        var directories = new HashSet<string> { root, srcDir };
        var entries = new Dictionary<string, List<string>>
        {
            [root] = new() { srcDir },
            [srcDir] = new() { Path.Combine(srcDir, "index.txt") },
        };
        var fs = new FakeFileSystem(entries, directories);
        var enumerator = new FileReferenceEntryEnumerator(fs, NullLogger.Instance);

        var results = enumerator.EnumerateEntries(srcDir, "src/", recursive: true)
            .Select(e => e.DisplayPath)
            .ToArray();

        Assert.Contains("src/index.txt", results);
        Assert.DoesNotContain("index.txt", results);
    }
}

internal sealed class FakeFileSystem : IFileReferenceFileSystem
{
    private readonly Dictionary<string, List<string>> _entries;
    private readonly HashSet<string> _directories;
    private readonly string? _failingDirectory;

    public int EnumerateFileSystemEntriesCallCount { get; private set; }

    public FakeFileSystem(
        Dictionary<string, List<string>> entries,
        HashSet<string> directories,
        string? failingDirectory = null)
    {
        _entries = entries;
        _directories = directories;
        _failingDirectory = failingDirectory;
    }

    public bool DirectoryExists(string path) => _directories.Contains(path);

    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        EnumerateFileSystemEntriesCallCount++;
        if (path == _failingDirectory)
            throw new UnauthorizedAccessException("Access denied");
        return _entries.TryGetValue(path, out var fileEntries) ? fileEntries : [];
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
}
