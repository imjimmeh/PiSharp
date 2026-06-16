using Xunit;

namespace PiSharp.Tui.Tests;

[Collection(TuiIntegrationTestCollection.Name)]
public sealed class TuiScenarioMetricsTests
{
    [Fact]
    public async Task ScenarioMetricsMeasuresPromptTypingBurst()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();

        var metrics = await TuiScenarioMetrics.MeasurePromptTypingBurstAsync(
            running,
            "hello",
            waitForIdle: host => host.TryUiThreadActionAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal("hello", metrics.FinalPromptText);
        Assert.True(metrics.Elapsed >= TimeSpan.Zero);
        Assert.True(metrics.UiResponsiveAfterBurst);
    }
}
