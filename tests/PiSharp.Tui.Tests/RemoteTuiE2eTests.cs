using Xunit;

namespace PiSharp.Tui.Tests;

[Collection(TuiIntegrationTestCollection.Name)]
public sealed class RemoteTuiE2eTests
{
    [Fact]
    public async Task E2E_PromptSubmission_OverRealWebSocket_RendersPromptAndAssistantResponseInTui()
    {
        await using var fixture = await RemoteTuiE2ETestFixture.StartAsync();

        var promptText = "Hello live daemon server";
        await fixture.SubmitPromptAsync(promptText);

        await fixture.RunningTui.WaitUntilAsync(
            screen => screen.Contains("Assistant") || screen.Contains("daemon status") || screen.Contains("task"),
            TimeSpan.FromSeconds(15));

        var screen = fixture.ScreenText;
        Assert.Contains(promptText, screen);
        Assert.True(screen.Contains("Assistant") || screen.Contains("daemon status") || screen.Contains("task"));
    }

    [Fact]
    public async Task E2E_SlashCommand_Help_ExecutesAndDisplaysHelpInTui()
    {
        await using var fixture = await RemoteTuiE2ETestFixture.StartAsync();

        await fixture.SubmitPromptAsync("/help");

        await fixture.RunningTui.WaitUntilAsync(
            screen => screen.Contains("Toggle right sidebar") || screen.Contains("Navigate prompt"),
            TimeSpan.FromSeconds(10));

        var screen = fixture.ScreenText;
        Assert.True(screen.Contains("Toggle right sidebar") || screen.Contains("Navigate prompt"));
    }
}
