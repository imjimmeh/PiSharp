using Xunit;
using PiSharp.Telemetry.Otlp;

namespace PiSharp.Telemetry.Otlp.Tests;

public sealed class InstallIdResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pisharp-otlp-tests-" + Guid.NewGuid().ToString("N"));

    public InstallIdResolverTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ReadsInstallId_FromPlanShape()
    {
        // Shape from plan §4.7: { schemaVersion, installId, createdAt, optedInAt }
        var path = Path.Combine(_dir, "telemetry.json");
        File.WriteAllText(path, """{"schemaVersion":1,"installId":"9f8e7d6c-5b4a-3210-fedc-ba9876543210","createdAt":"2026-08-14T10:00:00Z","optedInAt":"2026-08-14T10:00:00Z"}""");

        Assert.Equal("9f8e7d6c-5b4a-3210-fedc-ba9876543210", InstallIdResolver.TryRead(path));
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        Assert.Null(InstallIdResolver.TryRead(Path.Combine(_dir, "does-not-exist.json")));
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var path = Path.Combine(_dir, "telemetry.json");
        File.WriteAllText(path, "{ not json ]");
        Assert.Null(InstallIdResolver.TryRead(path));
    }

    [Fact]
    public void MissingInstallIdProperty_ReturnsNull()
    {
        var path = Path.Combine(_dir, "telemetry.json");
        File.WriteAllText(path, """{"schemaVersion":1}""");
        Assert.Null(InstallIdResolver.TryRead(path));
    }

    [Fact]
    public void BlankInstallId_ReturnsNull()
    {
        var path = Path.Combine(_dir, "telemetry.json");
        File.WriteAllText(path, """{"installId":"  "}""");
        Assert.Null(InstallIdResolver.TryRead(path));
    }

    [Fact]
    public void NonStringInstallId_ReturnsNull()
    {
        var path = Path.Combine(_dir, "telemetry.json");
        File.WriteAllText(path, """{"installId":42}""");
        Assert.Null(InstallIdResolver.TryRead(path));
    }

    [Fact]
    public void ResolveInstallIdPath_TargetsGlobalPiSharpDir()
    {
        var paths = PiSharp.Compatibility.Settings.PiAgentPaths.FromCwd(_dir);
        var expected = Path.Combine(paths.GlobalPiSharpDirectory, "telemetry.json");
        Assert.Equal(expected, InstallIdResolver.ResolveInstallIdPath(_dir));
    }
}
