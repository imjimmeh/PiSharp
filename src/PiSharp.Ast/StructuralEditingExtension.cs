using PiSharp.Tools;
using System.Text.Json;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ast.Host;
using PiSharp.Ast.Tools;
using PiSharp.Extensions;
using PiSharp.Ast.Ast;
using PiSharp.Ast.Ast.CSharp;

[assembly: ExtensionMetadata("pisharp-structural-editing", Name = "PiSharp Structural Editing", Version = "0.1.0")]

namespace PiSharp.Ast;

/// <summary>
/// P30 entry point. Registers <c>ast_grep</c>, <c>ast_edit</c>, <c>hashlines</c> and the
/// anchored <c>edit</c> override (OverrideBuiltIn) so existing edit call flows keep working.
/// </summary>
public sealed class StructuralEditingExtension : IExtension, IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private readonly StructuralEditingSettings _settings = new();

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        var env = new HostExecutionEnv(api.Cwd);
        _settings.ApplyFlags(api);

        if (_settings.HashlinesEnabled)
        {
            _registrations.Add(api.RegisterTool(RegistrationFor(new HashlinesTool(env))));
            _registrations.Add(api.RegisterTool(RegistrationFor(new HashlineEditTool(env), ExtensionOverridePolicy.OverrideBuiltIn)));
        }
        if (_settings.AstEnabled)
        {
            var registry = new AstLanguageRegistry();
            registry.Register(new CSharpAstProvider());
            _registrations.Add(api.RegisterTool(RegistrationFor(new AstGrepTool(env, registry, () => _settings.Enabled && _settings.AstEnabled))));
            _registrations.Add(api.RegisterTool(RegistrationFor(new AstEditTool(env, registry, () => _settings.Enabled && _settings.AstEnabled))));
        }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
        _registrations.Clear();
    }

    private static ExtensionToolRegistration RegistrationFor<TParameters, TDetails>(
        JsonTool<TParameters, TDetails> tool,
        ExtensionOverridePolicy overridePolicy = ExtensionOverridePolicy.Reject)
        where TParameters : class
    {
        return new ExtensionToolRegistration(
            tool.Name,
            tool.Label,
            tool.Description,
            tool.ParametersSchema,
            (toolCallId, parameters, ct, onUpdate) =>
                ((IAgentTool)tool).ExecuteAsync(toolCallId, parameters, ct, onUpdate),
            tool.ExecutionMode,
            tool.PromptSnippet,
            tool.PromptGuidelines,
            Override: overridePolicy);
    }
}

/// <summary>
/// P30 settings with P02-compatible defaults. The P02 Settings API is not yet present in the
/// host, so flags read through <see cref="IExtensionApi.GetFlag(string)"/> when available and
/// otherwise default on — P30 ships independently of P02 by design.
/// </summary>
public sealed class StructuralEditingSettings
{
    public bool Enabled { get; set; } = true;
    public bool AstEnabled { get; set; } = true;
    public bool HashlinesEnabled { get; set; } = true;
    public int AstMaxMatches { get; set; } = 100;

    public void ApplyFlags(IExtensionApi api)
    {
        var flags = api.GetFlags();
        if (flags.TryGetValue("enabled", out var enabled) && enabled is bool b) Enabled = b;
        if (flags.TryGetValue("ast.enabled", out var astEnabled) && astEnabled is bool ab) AstEnabled = ab;
        if (flags.TryGetValue("hashlines.enabled", out var hashEnabled) && hashEnabled is bool hb) HashlinesEnabled = hb;
        if (flags.TryGetValue("ast.max_matches", out var maxMatches) && maxMatches is JsonElement { ValueKind: JsonValueKind.Number } number)
            AstMaxMatches = number.GetInt32();
    }
}
