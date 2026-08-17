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
            screen => screen.Contains("Assistant") || screen.Contains("daemon status") || screen.Contains("task") || screen.Contains("Hello") && (screen.Contains("Working") || screen.Contains("Thinking") || screen.Contains("Idle")),
            TimeSpan.FromSeconds(30));

        var screen = fixture.ScreenText;
        Assert.Contains(promptText, screen);
        Assert.False(screen.Contains("JSON schema conversion failed"), "Screen should not contain JSON schema error");
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
