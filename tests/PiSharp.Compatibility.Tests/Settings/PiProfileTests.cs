using PiSharp.Compatibility.Settings;
using Xunit;

namespace PiSharp.Compatibility.Tests.Settings;

public sealed class PiProfileTests
{
    [Fact]
    public void Resolve_PrefersCliOverEnvironmentVariables()
    {
        var home = Path.Combine(Path.GetTempPath(), "pi-home-tests");
        var savedPrimary = Environment.GetEnvironmentVariable(PiProfiles.EnvironmentVariable);
        var savedLegacy = Environment.GetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, "from-primary");
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, "from-legacy");

            var profile = PiProfiles.Resolve("cli-name", home);

            Assert.NotNull(profile);
            Assert.Equal("cli-name", profile!.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, savedPrimary);
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, savedLegacy);
        }
    }

    [Fact]
    public void Resolve_FallsBackToPrimaryThenLegacyEnvironmentVariable()
    {
        var home = Path.Combine(Path.GetTempPath(), "pi-home-tests");
        var savedPrimary = Environment.GetEnvironmentVariable(PiProfiles.EnvironmentVariable);
        var savedLegacy = Environment.GetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, "legacy-name");

            var fromLegacy = PiProfiles.Resolve(null, home);
            Assert.Equal("legacy-name", fromLegacy!.Name);

            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, "primary-name");
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, "legacy-name");

            var fromPrimary = PiProfiles.Resolve(null, home);
            Assert.Equal("primary-name", fromPrimary!.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, savedPrimary);
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, savedLegacy);
        }
    }

    [Fact]
    public void Resolve_ComputesRootUnderProfilesDirectory()
    {
        var home = Path.Combine(Path.GetTempPath(), "pi-home-tests");
        var savedPrimary = Environment.GetEnvironmentVariable(PiProfiles.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, "work");

            var profile = PiProfiles.Resolve(null, home);

            Assert.NotNull(profile);
            Assert.Equal(Path.Combine(home, ".pi", "PiSharp", "profiles", "work"), profile!.RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, savedPrimary);
        }
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoProfileOrDefaultReservedName()
    {
        var savedPrimary = Environment.GetEnvironmentVariable(PiProfiles.EnvironmentVariable);
        var savedLegacy = Environment.GetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, null);

            Assert.Null(PiProfiles.Resolve(null, "C:\\home"));

            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, PiProfiles.DefaultProfileName);
            Assert.Null(PiProfiles.Resolve(null, "C:\\home"));

            Assert.Null(PiProfiles.Resolve(PiProfiles.DefaultProfileName, "C:\\home"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PiProfiles.EnvironmentVariable, savedPrimary);
            Environment.SetEnvironmentVariable(PiProfiles.LegacyEnvironmentVariable, savedLegacy);
        }
    }

    [Theory]
    [InlineData("work")]
    [InlineData("work-2")]
    [InlineData("a")]
    public void IsValidName_AcceptsValidNames(string name)
        => Assert.True(PiProfiles.IsValidName(name, out _));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("default")]
    [InlineData("UPPER")]
    [InlineData("with_underscore")]
    [InlineData("-leading-dash")]
    public void IsValidName_RejectsInvalidNames(string? name)
        => Assert.False(PiProfiles.IsValidName(name, out _));
}
