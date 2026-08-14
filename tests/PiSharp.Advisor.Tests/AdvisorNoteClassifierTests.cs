using Xunit;

namespace PiSharp.Advisor.Tests;

public class AdvisorNoteClassifierTests
{
    [Theory]
    [InlineData("this is a blocker, do not merge", "blocker")]
    [InlineData("You must revert this change", "blocker")]
    [InlineData("the fix cannot work as written", "blocker")]
    public void Classify_detects_blocker_language(string text, string expected)
        => Assert.Equal(expected, AdvisorNoteClassifier.Classify(text));

    [Theory]
    [InlineData("there is a risk of regressions", "concern")]
    [InlineData("be careful with this lookup", "concern")]
    [InlineData("this could over-allocate", "concern")]
    public void Classify_detects_concern_language(string text, string expected)
        => Assert.Equal(expected, AdvisorNoteClassifier.Classify(text));

    [Theory]
    [InlineData("the build is green", "note")]
    [InlineData("looks consistent with the surrounding code", "note")]
    [InlineData("", "note")]
    [InlineData("   ", "note")]
    public void Classify_returns_note_default(string text, string expected)
        => Assert.Equal(expected, AdvisorNoteClassifier.Classify(text));
}
