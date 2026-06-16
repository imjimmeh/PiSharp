using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class ExtensionAssemblyIdentityTests
{
    [Fact]
    public void ExtensionContractAssemblyVersionStaysCompatibleWithInstalledNativeExtensions()
    {
        Assert.Equal(new Version(1, 0, 0, 0), typeof(IExtension).Assembly.GetName().Version);
    }
}
