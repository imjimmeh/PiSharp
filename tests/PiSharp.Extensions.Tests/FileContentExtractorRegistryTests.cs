using PiSharp.Agent.Core.Tools;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class FileContentExtractorRegistryTests
{
    private sealed class StubExtractor(string id, string? text = null) : IFileContentExtractor
    {
        public string Id => id;
        public bool CanHandle(string path, ReadOnlySpan<byte> bytes) => true;
        public Task<FileContentExtractionResult?> ExtractAsync(string path, ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
            => Task.FromResult(text is null ? null : new FileContentExtractionResult(text));
    }

    [Fact]
    public void Register_ThenExtractorsListedInOrder()
    {
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor("a"));
        registry.Register(new StubExtractor("b"));
        registry.Register(new StubExtractor("c"));

        Assert.Equal(["a", "b", "c"], registry.Extractors.Select(e => e.Id));
    }

    [Fact]
    public void Register_DuplicateIdThrows()
    {
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor("pdf"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new StubExtractor("pdf")));
    }

    [Fact]
    public async Task Register_DuplicateIdWithOverrideReplacesKeepingPosition()
    {
        var registry = new FileContentExtractorRegistry();
        var original = new StubExtractor("pdf", "original");
        registry.Register(new StubExtractor("a"));
        registry.Register(original);
        registry.Register(new StubExtractor("b"));

        registry.Register(new StubExtractor("pdf", "replacement"), overrideExisting: true);

        Assert.Equal(["a", "pdf", "b"], registry.Extractors.Select(e => e.Id));
        var replaced = await registry.Extractors.Single(e => e.Id == "pdf").ExtractAsync("x", default);
        Assert.Equal("replacement", replaced!.Text);
    }

    [Fact]
    public void Unregister_RemovesExtractor()
    {
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor("pdf"));
        registry.Register(new StubExtractor("docx"));

        Assert.True(registry.Unregister("pdf"));
        Assert.Equal(["docx"], registry.Extractors.Select(e => e.Id));
        Assert.False(registry.Unregister("pdf"));
    }

    [Fact]
    public void Unregister_IsCaseInsensitive()
    {
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor("pdf"));

        Assert.True(registry.Unregister("PDF"));
    }

    [Fact]
    public void Register_EmptyIdThrows()
    {
        var registry = new FileContentExtractorRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new StubExtractor("")));
    }
}
