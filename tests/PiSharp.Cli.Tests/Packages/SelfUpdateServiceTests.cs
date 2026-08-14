using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class SelfUpdateServiceTests
{
    [Fact]
    public async Task UpdateAsync_DelegatesToMethod()
    {
        var method = new FakeSelfUpdateMethod(new SelfUpdateResult(Updated: true, AlreadyUpToDate: false, InstalledVersion: "1.2.3"));
        var service = new SelfUpdateService(method);

        var result = await service.UpdateAsync(addSource: null, offline: false, CancellationToken.None);

        Assert.True(result.Updated);
        Assert.Equal("1.2.3", result.InstalledVersion);
        Assert.Null(method.LastAddSource);
    }

    [Fact]
    public async Task UpdateAsync_PassesAddSourceToMethod()
    {
        var method = new FakeSelfUpdateMethod(new SelfUpdateResult(Updated: false, AlreadyUpToDate: true));
        var service = new SelfUpdateService(method);

        await service.UpdateAsync(addSource: "https://feed.local/v3/index.json", offline: false, CancellationToken.None);

        Assert.Equal("https://feed.local/v3/index.json", method.LastAddSource);
    }

    [Fact]
    public async Task PrintDaemonNoticeAsync_NoOp_WhenLeaseFileAbsent()
    {
        var service = new SelfUpdateService(new FakeSelfUpdateMethod(new SelfUpdateResult(Updated: true, AlreadyUpToDate: false)));
        var writer = new StringWriter();

        await service.PrintDaemonNoticeAsync("/nonexistent/lease.json", writer, CancellationToken.None);

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public async Task PrintDaemonNoticeAsync_WritesNotice_WhenLeaseFileExists()
    {
        var service = new SelfUpdateService(new FakeSelfUpdateMethod(new SelfUpdateResult(Updated: true, AlreadyUpToDate: false)));
        var leasePath = Path.Combine(Path.GetTempPath(), $"pisharp-lease-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(leasePath, "{}");
            var writer = new StringWriter();

            await service.PrintDaemonNoticeAsync(leasePath, writer, CancellationToken.None);

            Assert.Contains("daemon", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(leasePath)) File.Delete(leasePath);
        }
    }

    private sealed class FakeSelfUpdateMethod(SelfUpdateResult result) : ISelfUpdateMethod
    {
        public string? LastAddSource { get; private set; }
        public SelfUpdateMethodKind Kind => SelfUpdateMethodKind.DotnetTool;
        public bool CanUpdate => true;
        public string ManualInstructions => string.Empty;

        public Task<SelfUpdateResult> UpdateAsync(string? addSource, bool offline, CancellationToken cancellationToken)
        {
            LastAddSource = addSource;
            return Task.FromResult(result);
        }
    }
}
