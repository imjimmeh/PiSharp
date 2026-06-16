using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;
using Xunit;

namespace PiSharp.Agent.Tests.Resources.Prompting;

public sealed class PromptDebugViewTests
{
    [Fact]
    public void FromDocumentIncludesSectionMetadataAndDiagnostics()
    {
        var source = new PromptContributionSource("extension:test", PromptContributionSourceKind.Extension);
        var section = new PromptSection("id", PromptSectionKind.Extension, new RawPromptContent("body"), new PromptPlacement("footer", 7));
        var diagnostic = new PromptDiagnostic("duplicate_section", "duplicate", "id", "extension:test");
        var document = new SystemPromptDocument([section], [diagnostic], new Dictionary<string, PromptContributionSource> { ["id"] = source });

        var view = PromptDebugView.FromDocument(document);

        var debugSection = Assert.Single(view.Sections);
        Assert.Equal("id", debugSection.Id);
        Assert.Equal("footer", debugSection.Slot);
        Assert.Equal(7, debugSection.Priority);
        Assert.Equal("Extension", debugSection.Kind);
        Assert.Equal("extension:test", debugSection.SourceId);
        Assert.Same(diagnostic, Assert.Single(view.Diagnostics));
    }
}
