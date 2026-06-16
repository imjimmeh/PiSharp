using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class PkceHelperTests
{
    [Fact]
    public void GeneratesValidVerifierAndChallenge()
    {
        var (verifier, challenge) = PkceHelper.Generate();

        Assert.NotNull(verifier);
        Assert.NotNull(challenge);
        Assert.True(verifier.Length >= 43);
        Assert.True(verifier.Length <= 128);
        Assert.DoesNotContain("=", challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
    }

    [Fact]
    public void GeneratesUniqueValuesEachCall()
    {
        var (v1, c1) = PkceHelper.Generate();
        var (v2, c2) = PkceHelper.Generate();
        Assert.NotEqual(v1, v2);
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void ChallengeIsDerivedFromVerifier()
    {
        var (verifier, challenge) = PkceHelper.Generate();
        Assert.NotEqual(verifier, challenge);
    }
}
