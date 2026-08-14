using System.Text.Json;
using PiSharp.Eval.Kernels;

namespace PiSharp.Eval.Kernel.CSharp;

/// <summary>
/// Typed loopback helpers callable from kernel code. Each helper serializes its arguments
/// to the built-in tool input record (snake_case, the web serializer conventions the tools
/// use) and returns the tool's content string. Unallowed or failing tools return a normal
/// error string — never an exception — mirroring <see cref="IKernelToolBridge"/> semantics.
/// </summary>
public sealed class KernelTools
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IKernelToolBridge? _bridge;

    internal KernelTools(IKernelToolBridge? bridge)
    {
        _bridge = bridge;
    }

    public Task<string> Read(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => Run("read", new { path, offset, limit }, ct);

    public Task<string> Grep(string pattern, string? path = null, bool? ignoreCase = null, int? limit = null, CancellationToken ct = default)
        => Run("grep", new { pattern, path, ignoreCase, limit }, ct);

    public Task<string> Find(string pattern, string? path = null, int? limit = null, CancellationToken ct = default)
        => Run("find", new { pattern, path, limit }, ct);

    public Task<string> List(string? path = null, int? limit = null, CancellationToken ct = default)
        => Run("ls", new { path, limit }, ct);

    /// <summary>Executes any allowlisted tool by name with arbitrary (JSON-serializable) arguments.</summary>
    public Task<string> Run(string toolName, JsonElement arguments, CancellationToken ct = default)
        => InvokeAsync(toolName, arguments, ct);

    public Task<string> Run(string toolName, object? arguments, CancellationToken ct = default)
        => InvokeAsync(toolName, JsonSerializer.SerializeToElement(arguments, WebJsonOptions), ct);

    private async Task<string> InvokeAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        if (_bridge is null)
            return "Error: tool loopback is disabled for this kernel.";
        if (!_bridge.AvailableToolNames.Contains(toolName, StringComparer.Ordinal))
            return $"Error: tool '{toolName}' is not allowed for eval loopback.";

        var result = await _bridge.ExecuteToolAsync(toolName, arguments, ct);
        if (!result.Ok)
            return $"Error: {result.Error}";
        return result.Content as string ?? string.Empty;
    }
}
