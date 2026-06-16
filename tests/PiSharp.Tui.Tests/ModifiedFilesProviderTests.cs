using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class ModifiedFilesProviderTests
{
    [Fact]
    public void Parse_ReturnsRelativePaths_StrippingStatusPrefix()
    {
        var output = " M src/A.cs\n?? new/B.txt\nA  added/C.cs\n";

        var files = ModifiedFilesProvider.Parse(output);

        Assert.Equal(new[] { "src/A.cs", "new/B.txt", "added/C.cs" }, files);
    }

    [Fact]
    public void Parse_HandlesRenames_UsingDestinationPath()
    {
        var output = "R  old.cs -> new.cs\n";

        var files = ModifiedFilesProvider.Parse(output);

        Assert.Equal(new[] { "new.cs" }, files);
    }

    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(ModifiedFilesProvider.Parse(""));
        Assert.Empty(ModifiedFilesProvider.Parse("   \n"));
    }
}
