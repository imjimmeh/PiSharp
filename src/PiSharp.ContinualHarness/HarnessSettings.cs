using PiSharp.Extensions;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Gating/config surface for the continual-harness plugin, read from
/// <c>extensions.pisharp-refine.*</c> settings with P02-style defaults. Abstracted so tests can
/// substitute a stub and so the master gate can be honored at init.
/// </summary>
public interface IHarnessSettings
{
    /// <summary>Master gate; disabled -> the extension registers nothing.</summary>
    bool Enabled { get; }

    /// <summary>Register the model-facing <c>refine</c> tool.</summary>
    bool ToolEnabled { get; }

    /// <summary>Reject records with no evidence citations.</summary>
    bool RequireEvidence { get; }

    /// <summary>"ask" (Ui confirm) | "reject" (never clobber) | "force" (auto-clobber).</summary>
    string ConflictPolicy { get; }

    /// <summary>"local" | "global".</summary>
    string DefaultScope { get; }

    /// <summary>Which kinds /refine may touch (defense in depth).</summary>
    IReadOnlyList<string> AllowedKinds { get; }

    /// <summary>Bound on content payload bytes per record.</summary>
    int MaxContentBytes { get; }
}

/// <summary>
/// Default <see cref="IHarnessSettings"/> backed by <see cref="IExtensionSettingsApi"/>.
/// Keys are read under the plugin's own "pisharp-refine" namespace.
/// </summary>
public sealed class HarnessSettings(IExtensionSettingsApi api) : IHarnessSettings
{
    public const string ExtensionId = "pisharp-refine";

    public bool Enabled => api.Get<bool?>("enabled") ?? true;
    public bool ToolEnabled => api.Get<bool?>("toolEnabled") ?? true;
    public bool RequireEvidence => api.Get<bool?>("requireEvidence") ?? true;
    public string ConflictPolicy => api.Get<string?>("conflictPolicy") ?? "ask";
    public string DefaultScope => api.Get<string?>("defaultScope") ?? "local";

    public IReadOnlyList<string> AllowedKinds
    {
        get
        {
            var value = api.Get<System.Text.Json.JsonElement?>("allowedKinds");
            if (value is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var result = new List<string>();
                foreach (var item in el.EnumerateArray()) result.Add(item.GetString()!);
                return result;
            }
            return ["prompt", "memory", "skill", "subagent"];
        }
    }

    public int MaxContentBytes => api.Get<int?>("maxContentBytes") ?? 65536;
}
