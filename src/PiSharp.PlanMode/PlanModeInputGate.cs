using PiSharp.Extensions;

namespace PiSharp.PlanMode;

/// <summary>
/// Subscribes <c>input</c>; while <see cref="PlanModePhase.Planning"/>, transforms prompts that
/// demand file/state changes into planning-appropriate requests (defense in depth). User
/// <c>!bash</c> lines are left unrestricted (user intent, not model action — plan §13.3).
/// </summary>
public sealed class PlanModeInputGate(PlanModeService service)
{
    private const string PlanningHint =
        "[plan mode] You are in the read-only planning phase. Explore and investigate instead of changing files; end with a complete plan as your final message.";

    private static readonly string[] MutationDirectives =
    [
        "edit ", "write ", "create ", "delete ", "remove ", "modify ", "update ",
        "refactor ", "rename ", "move ", "copy ", "implement ", "fix ", "commit ",
        "push ", "merge ", "install ", "run ", "build ", "format "
    ];

    public Task OnInputAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (service.Phase != PlanModePhase.Planning) return Task.CompletedTask;
        if (evt.Payload is not ExtensionInputEvent input) return Task.CompletedTask;

        var text = input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        if (text.StartsWith('!')) return Task.CompletedTask;

        if (RequiresTransform(text))
            evt.TransformInput(text + "\n\n" + PlanningHint);

        return Task.CompletedTask;
    }

    /// <summary>True when the prompt contains a state-changing directive keyword.</summary>
    public static bool RequiresTransform(string text)
        => MutationDirectives.Any(directive => text.Contains(directive, StringComparison.OrdinalIgnoreCase));
}
