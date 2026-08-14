using PiSharp.Extensions;

namespace PiSharp.Git;

/// <summary>
/// The narrow set of extension-host capabilities slash commands need. Captured once at
/// <see cref="GitExtension.InitializeAsync"/> (the command-handler signature only receives
/// args), it is also the seam tests use to drive the commands with fakes.
/// </summary>
public sealed record CommandHost(
    IExtensionUi Ui,
    bool HasUi,
    string Cwd,
    Func<string, CancellationToken, Task> SendMessageAsync);

/// <summary>Helpers shared by the slash-command handlers.</summary>
internal static class CommandHostBuilder
{
    public static CommandHost FromApi(IExtensionApi api)
        => new(
            api.Ui,
            api.HasUi,
            api.Cwd,
            (text, ct) => api.SendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.User(text), ct));
}
