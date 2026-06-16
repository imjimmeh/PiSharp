using PiSharp.Cli.Files;
using Xunit;

namespace PiSharp.Cli.Tests.Files;

public sealed class FileReferenceProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pisharp-at-files-" + Guid.NewGuid().ToString("N"));

    public FileReferenceProcessorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InlineAtFileReferencePrependsFileContentToPrompt()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello from file");

        var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync("Summarize @notes.txt", _root, CancellationToken.None);

        Assert.Contains("<file name=\"", processed.Text);
        Assert.Contains("notes.txt", processed.Text);
        Assert.Contains("hello from file", processed.Text);
        Assert.Contains("Summarize @notes.txt", processed.Text);
        Assert.Empty(processed.Images);
    }

    [Fact]
    public async Task InlineAtFileReferenceSupportsQuotedPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "my folder"));
        File.WriteAllText(Path.Combine(_root, "my folder", "notes.txt"), "quoted content");

        var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync("Read @\"my folder/notes.txt\"", _root, CancellationToken.None);

        Assert.Contains("quoted content", processed.Text);
        Assert.Contains("Read @\"my folder/notes.txt\"", processed.Text);
    }

    [Fact]
    public async Task InlineAtMentionIsLeftAloneWhenNoMatchingFileExists()
    {
        var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync("Ask @person about this", _root, CancellationToken.None);

        Assert.Equal("Ask @person about this", processed.Text);
        Assert.Empty(processed.Images);
    }

    [Fact]
    public async Task InlineAtFileReferenceSkipsGitIgnoredFiles()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.txt" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_root, "ignored.txt"), "secret content");
        await RunGitAsync("init");

        var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync("Summarize @ignored.txt", _root, CancellationToken.None);

        Assert.Equal("Summarize @ignored.txt", processed.Text);
        Assert.DoesNotContain("secret content", processed.Text);
        Assert.Empty(processed.Images);
    }

    [Fact]
    public async Task InlineAtFileReferenceSkipsFilesOutsideWorkingDirectory()
    {
        var sibling = _root + "-sibling";
        try
        {
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "outside.txt"), "outside content");

            var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync("Summarize @../" + Path.GetFileName(sibling) + "/outside.txt", _root, CancellationToken.None);

            Assert.DoesNotContain("outside content", processed.Text);
            Assert.Equal("Summarize @../" + Path.GetFileName(sibling) + "/outside.txt", processed.Text);
            Assert.Empty(processed.Images);
        }
        finally
        {
            if (Directory.Exists(sibling)) Directory.Delete(sibling, recursive: true);
        }
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
