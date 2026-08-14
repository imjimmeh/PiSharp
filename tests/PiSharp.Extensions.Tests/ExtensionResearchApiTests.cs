using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Verifies the P28 <see cref="IExtensionApi.Files"/> and
/// <see cref="IExtensionApi.Search"/> registration surfaces end-to-end: a real
/// extension initialized through <see cref="ExtensionManager"/> lands its
/// extractor/provider in the runtime-wide registries wired onto the binding,
/// covers duplicate rejection and disposable unregister, and reflects on
/// <see cref="FakeExtensionApi"/>.
/// </summary>
public sealed class ExtensionResearchApiTests
{
    private sealed class StubExtractor(string id = "pdf") : IFileContentExtractor
    {
        public string Id => id;
        public bool CanHandle(string path, ReadOnlySpan<byte> bytes) => true;
        public Task<FileContentExtractionResult?> ExtractAsync(string path, ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
            => Task.FromResult<FileContentExtractionResult?>(null);
    }

    private sealed class StubProvider(string id = "serper") : ISearchProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new SearchResponse(id, []));
    }

    private sealed class RegisteringExtension(
        Action<IExtensionApi>? onInitialize = null) : IExtension
    {
        private readonly Action<IExtensionApi>? _onInitialize = onInitialize;
        public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
        {
            _onInitialize?.Invoke(api);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Files_ExtensionRegisteration_LandsInBindingRegistry()
    {
        var extractors = new FileContentExtractorRegistry();
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance) { FileContentExtractors = extractors };
        var manager = new ExtensionManager(new ExtensionRegistry());
        var registered = false;

        await manager.InitializeAsync(
            new ExtensionDescriptor("research", "Research", "1.0.0"),
            new RegisteringExtension(api =>
            {
                api.Files.RegisterContentExtractor(new StubExtractor());
                registered = true;
            }),
            binding);

        Assert.True(registered);
        var extractor = Assert.Single(extractors.Extractors);
        Assert.Equal("pdf", extractor.Id);
    }

    [Fact]
    public async Task Search_ExtensionRegistration_LandsInBindingRegistry()
    {
        var providers = new SearchProviderRegistry();
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance) { SearchProviders = providers };
        var manager = new ExtensionManager(new ExtensionRegistry());
        var registered = false;

        await manager.InitializeAsync(
            new ExtensionDescriptor("research", "Research", "1.0.0"),
            new RegisteringExtension(api =>
            {
                api.Search.RegisterProvider(new StubProvider());
                registered = true;
            }),
            binding);

        Assert.True(registered);
        Assert.Equal("serper", providers.TryGet("serper")!.Id);
    }

    [Fact]
    public async Task Files_DuplicateExtractorId_ThrowsUnlessOverride()
    {
        var extractors = new FileContentExtractorRegistry();
        extractors.Register(new StubExtractor("pdf"));
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance) { FileContentExtractors = extractors };
        var manager = new ExtensionManager(new ExtensionRegistry());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InitializeAsync(
                new ExtensionDescriptor("research", "Research", "1.0.0"),
                new RegisteringExtension(api => api.Files.RegisterContentExtractor(new StubExtractor("pdf"))),
                binding));

        // Override succeeds.
        await manager.InitializeAsync(
            new ExtensionDescriptor("research2", "Research 2", "1.0.0"),
            new RegisteringExtension(api => api.Files.RegisterContentExtractor(new StubExtractor("pdf"), overrideExisting: true)),
            binding);

        Assert.Single(extractors.Extractors);
    }

    [Fact]
    public async Task Files_DisposableUnregisters()
    {
        var extractors = new FileContentExtractorRegistry();
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance) { FileContentExtractors = extractors };
        var manager = new ExtensionManager(new ExtensionRegistry());
        IDisposable? handle = null;

        await manager.InitializeAsync(
            new ExtensionDescriptor("research", "Research", "1.0.0"),
            new RegisteringExtension(api => handle = api.Files.RegisterContentExtractor(new StubExtractor())),
            binding);

        Assert.Single(extractors.Extractors);
        handle!.Dispose();
        Assert.Empty(extractors.Extractors);
    }

    [Fact]
    public async Task Files_WithoutRegistry_ThrowsNotSupported()
    {
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance); // no registry
        var manager = new ExtensionManager(new ExtensionRegistry());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.InitializeAsync(
                new ExtensionDescriptor("research", "Research", "1.0.0"),
                new RegisteringExtension(api => api.Files.RegisterContentExtractor(new StubExtractor())),
                binding));
    }

    [Fact]
    public void FakeExtensionApi_PromotesFilesAndSearchCapturedLists()
    {
        var api = new FakeExtensionApi();
        var extractor = new StubExtractor("pdf");
        var provider = new StubProvider("serper");

        api.Files.RegisterContentExtractor(extractor);
        api.Search.RegisterProvider(provider);

        Assert.Single(api.RegisteredContentExtractors);
        Assert.Equal("pdf", api.RegisteredContentExtractors[0].Id);
        Assert.Single(api.RegisteredSearchProviders);
        Assert.Equal("serper", api.RegisteredSearchProviders[0].Id);
        Assert.Same(provider, api.Search.GetProvider("serper"));
        Assert.Same(extractor, api.Files.ContentExtractors[0]);
    }

    [Fact]
    public void FakeExtensionApi_RejectsDuplicateRegistration()
    {
        var api = new FakeExtensionApi();
        api.Files.RegisterContentExtractor(new StubExtractor("pdf"));
        api.Search.RegisterProvider(new StubProvider("serper"));

        Assert.Throws<InvalidOperationException>(() => api.Files.RegisterContentExtractor(new StubExtractor("pdf")));
        Assert.Throws<InvalidOperationException>(() => api.Search.RegisterProvider(new StubProvider("serper")));
    }
}
