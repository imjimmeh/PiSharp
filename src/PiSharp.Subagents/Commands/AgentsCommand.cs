using System.Text;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;
using PiSharp.Subagents.Discovery;

namespace PiSharp.Subagents.Commands;

/// <summary>
/// The <c>/agents</c> slash command (alias <c>/subagents</c>): lists visible agent definitions,
/// marking <c>hide</c>d and disabled entries. Rendered as a user message so any host surfaces it.
/// </summary>
public static class AgentsCommand
{
    public const string Name = "agents";
    public const string Alias = "subagents";

    public static ExtensionCommandRegistration Create(AgentDefinitionRegistry registry, IExtensionApi api)
        => new(Name, "Lists available subagent definitions.", (_, cancellationToken) => RenderAsync(registry, api, cancellationToken));

    public static ExtensionCommandRegistration CreateAlias(AgentDefinitionRegistry registry, IExtensionApi api)
        => new(Alias, "Lists available subagent definitions (alias of /agents).", (_, cancellationToken) => RenderAsync(registry, api, cancellationToken));

    private static async Task RenderAsync(AgentDefinitionRegistry registry, IExtensionApi api, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Agents");
        var visible = registry.ListVisible();
        if (visible.Count == 0)
        {
            builder.AppendLine("_No agent definitions found._");
        }
        else
        {
            foreach (var definition in visible)
            {
                builder.Append("- **").Append(definition.Name).Append("** — ").AppendLine(definition.Description);
                if (registry.IsDisabled(definition.Name))
                    builder.AppendLine("  - `disabled`");
                if (definition.Spawns.Count == 0)
                    builder.AppendLine("  - `no-spawning`");
            }
        }

        await api.SendMessageAsync(AgentMessages.User(builder.ToString().TrimEnd()), cancellationToken: cancellationToken);
    }
}
