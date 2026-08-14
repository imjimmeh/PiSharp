using PiSharp.Memory.Abstractions;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class MemoryProjectKeysTests
{
    [Fact]
    public void Encode_NormalizesWindowsPathToSessionDirConvention()
    {
        // Matches PiAgentPaths/SessionRepoUtils.EncodeCwd: leading separators trimmed,
        // path separators and ':' become '-', wrapped in --...--.
        Assert.Equal("--C--code-AI-pi-PiSharp--", MemoryProjectKeys.Encode(@"C:\code\AI\pi\PiSharp"));
    }

    [Fact]
    public void Encode_NormalizesPosixPath()
    {
        Assert.Equal("--home-user-proj--", MemoryProjectKeys.Encode("/home/user/proj"));
    }

    [Fact]
    public void Encode_DifferentCwdsProduceDifferentKeys()
    {
        var a = MemoryProjectKeys.Encode(@"C:\proj\one");
        var b = MemoryProjectKeys.Encode(@"C:\proj\two");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encode_TrailingSeparatorBecomesDash()
    {
        // The shared convention only trims leading separators; a trailing separator
        // folds into a dash (no empty segment loss).
        Assert.Equal("--C--proj---", MemoryProjectKeys.Encode(@"C:\proj\"));
    }

    [Fact]
    public void Encode_EmptyCwd_ProducesBareWrapper()
    {
        Assert.Equal("----", MemoryProjectKeys.Encode(string.Empty));
    }
}
