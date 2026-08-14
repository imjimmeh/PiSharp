using System.ComponentModel;
using PiSharp.Extensions;

namespace PiSharp.AgentMessaging;

/// <summary>
/// Settings for the agent-messaging surface. Read from the
/// <c>extensions.agent-messaging.*</c> settings namespace with the plan's
/// defaults; a settings value of null falls back to the default.
/// </summary>
public sealed record AgentMessagingOptions
{
    /// <summary>Master switch for the plugin + routing.</summary>
    [Description("Master switch for agent messaging.")]
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the messaging brief is appended on before_prompt_render.</summary>
    [Description("Whether the messaging brief is appended on before_prompt_render.")]
    public bool BriefInPrompt { get; init; } = true;

    /// <summary>Maximum message body length.</summary>
    [Description("Maximum agent-message body length.")]
    public int MaxInboxMessageLength { get; init; } = 8192;

    /// <summary>Hours a message to a Passivated/Gone agent survives before failing.</summary>
    [Description("Hours a queued message survives before it fails.")]
    public int QueuedMessageTtlHours { get; init; } = 24;

    /// <summary>Roster roles the hub tool may target for send/steer.</summary>
    [Description("Roster roles the hub tool may target for send/steer.")]
    public IReadOnlyList<string> HubRoleWhitelist { get; init; } = ["main", "subagent"];

    /// <summary>Directory for the persisted JSONL outbox of undelivered messages.</summary>
    public string? StoreDirectory { get; init; }

    internal const string SettingsPrefix = "agentMessaging";

    public static AgentMessagingOptions Read(IExtensionApi api, string? defaultStoreDirectory = null)
    {
        return new AgentMessagingOptions
        {
            Enabled = api.Settings.Get<bool?>(SettingsPrefix + ".enabled") ?? true,
            BriefInPrompt = api.Settings.Get<bool?>(SettingsPrefix + ".briefInPrompt") ?? true,
            MaxInboxMessageLength = api.Settings.Get<int?>(SettingsPrefix + ".maxInboxMessageLength") ?? 8192,
            QueuedMessageTtlHours = api.Settings.Get<int?>(SettingsPrefix + ".queuedMessageTtlHours") ?? 24,
            HubRoleWhitelist = api.Settings.Get<IReadOnlyList<string>?>(SettingsPrefix + ".hubRoleWhitelist") ?? ["main", "subagent"],
            StoreDirectory = api.Settings.Get<string?>(SettingsPrefix + ".storeDirectory") ?? defaultStoreDirectory,
        };
    }
}
