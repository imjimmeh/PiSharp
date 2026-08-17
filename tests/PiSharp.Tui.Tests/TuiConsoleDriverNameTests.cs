using System.Text;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiConsoleDriverNameTests
{
    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "")]
    public void TuiDriverSelectionDefaultsToPlatformDriver(bool isWindows, string expectedDriverName)
    {
        Assert.Equal(expectedDriverName, TuiConsoleDriverName.DefaultForPlatform(isWindows));
    }

    [Fact]
    public void DriverPreparationForcesUtf8BeforeTerminalGuiInitializes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding current = Encoding.GetEncoding(437, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);

        TuiConsoleDriverName.PrepareConsoleForDriver(string.Empty, () => current, encoding => current = encoding);

        Assert.Equal(Encoding.UTF8.CodePage, current.CodePage);
    }

    [Fact]
    public void NonUtf8ConsoleEncodingReplacesUnicodeUiCharactersThatAppearAfterExtensionsLoad()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var nonUtf8 = Encoding.GetEncoding(
            437,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);

        var rendered = nonUtf8.GetString(nonUtf8.GetBytes("╔═ • ⠙ ● ↑↓"));

        Assert.NotEqual("╔═ • ⠙ ● ↑↓", rendered);
        Assert.Equal("╔═ ? ? ? ??", rendered);
    }

    [Fact]
    public void Utf8EncodingCanRepresentUnicodeUiCharactersUsedAfterExtensionsLoad()
    {
        var rendered = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("╔═ • ⠙ ● ↑↓"));

        Assert.Equal("╔═ • ⠙ ● ↑↓", rendered);
        Assert.DoesNotContain('�', rendered);
    }
}
