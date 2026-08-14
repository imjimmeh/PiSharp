using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Advisor.Tests;

public class AdvisorSettingsTests
{
    [Fact]
    public void Read_defaults_to_off_with_no_settings()
    {
        var settings = new AdvisorSettings(new TestSettingsApi());

        var options = settings.Read();

        Assert.False(options.Enabled);
        Assert.Null(options.Model);
        Assert.Equal(10000, options.TimeoutMs);
        Assert.Equal(512, options.MaxTokens);
        Assert.Equal(12, options.MaxTranscriptTurns);
        Assert.True(options.Coalesce);
    }

    [Fact]
    public void Read_honors_bare_namespaced_keys()
    {
        var api = new TestSettingsApi();
        api.SetAsync("enabled", true);
        api.SetAsync("model", "claude-haiku-4-5");
        api.SetAsync("timeoutMs", 5000);
        api.SetAsync("maxTokens", 128);
        api.SetAsync("maxTranscriptTurns", 4);
        api.SetAsync("coalesce", false);

        var options = new AdvisorSettings(api).Read();

        Assert.True(options.Enabled);
        Assert.Equal("claude-haiku-4-5", options.Model);
        Assert.Equal(5000, options.TimeoutMs);
        Assert.Equal(128, options.MaxTokens);
        Assert.Equal(4, options.MaxTranscriptTurns);
        Assert.False(options.Coalesce);
    }

    [Fact]
    public void Read_accepts_plan_form_advisor_prefixed_keys()
    {
        var api = new TestSettingsApi();
        api.SetAsync("advisor.enabled", true);
        api.SetAsync("advisor.model", "fp/test");
        api.SetAsync("advisor.maxTokens", 64);

        var options = new AdvisorSettings(api).Read();

        Assert.True(options.Enabled);
        Assert.Equal("fp/test", options.Model);
        Assert.Equal(64, options.MaxTokens);
    }
}
