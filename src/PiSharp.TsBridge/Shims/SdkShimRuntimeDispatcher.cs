using System.Text.Json;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge.Shims;

public sealed class SdkShimRuntimeDispatcher
{
    private const string SdkPrefix = "sdk.";

    public static bool CanHandle(string action) => action.StartsWith(SdkPrefix, StringComparison.Ordinal);

    public TsRuntimeActionResult? TryResolve(string action, out string mappedRuntimeAction)
    {
        mappedRuntimeAction = string.Empty;
        var exportName = action.Substring(SdkPrefix.Length);

        if (!SdkShimExportClassification.All.TryGetValue(exportName, out var definition))
            return new TsRuntimeActionResult(Ok: false,
                Error: $"Pi coding-agent SDK export '{exportName}' is not implemented by PiSharp.");

        if (definition.Status == SdkShimExportStatus.Unsupported)
            return new TsRuntimeActionResult(Ok: false,
                Error: $"Pi coding-agent SDK export '{exportName}' is not implemented by PiSharp.");

        if (definition.Status == SdkShimExportStatus.Stubbed)
            return new TsRuntimeActionResult(GetStubValue(definition));

        if (definition.RuntimeAction is not null)
        {
            mappedRuntimeAction = definition.RuntimeAction;
            return null;
        }

        return new TsRuntimeActionResult(Ok: false,
            Error: $"Pi coding-agent SDK export '{exportName}' is not implemented by PiSharp.");
    }

    public Task<TsRuntimeActionResult?> HandleAsync(string action, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryResolve(action, out _));
    }

    private static object? GetStubValue(SdkShimExportDefinition definition)
    {
        if (definition.Value is not null)
            return definition.Value;

        if (definition.Helper is not null)
        {
            return definition.Helper switch
            {
                "emptyArrayFunction" => Array.Empty<object>(),
                "emptyObjectFunction" => new { },
                "DynamicBorder" or "DefaultResourceLoader" or "SessionManager" or "SettingsManager" or "AgentSession" => new { },
                _ => new { }
            };
        }

        return new { };
    }
}
