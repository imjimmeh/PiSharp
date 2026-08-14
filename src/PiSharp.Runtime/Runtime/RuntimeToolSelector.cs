using PiSharp.Abstractions.Environment;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Tools;

namespace PiSharp.Runtime;

public sealed record RuntimeToolSelection(IReadOnlyList<IAgentTool> Tools, IReadOnlyList<string>? ActiveToolNames);

public static class RuntimeToolSelector
{
    public static RuntimeToolSelection Create(IExecutionEnv env, RuntimeToolOptions? options, ToolsOptions? toolOptions = null, InternalUrlRegistry? urlRegistry = null, FileContentExtractorRegistry? contentExtractors = null)
    {
        options ??= new RuntimeToolOptions();
        if (options.DisableAll || options.DisableBuiltIns) return new RuntimeToolSelection([], null);
        var all = BuiltInTools.CreateAll(env, toolOptions, urlRegistry, contentExtractors);
        if (options.ActiveToolNames is null || options.ActiveToolNames.Count == 0) return new RuntimeToolSelection(all.Values.ToArray(), null);
        var active = new List<string>();
        foreach (var name in options.ActiveToolNames.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (!all.ContainsKey(name)) throw new ArgumentException($"Unknown tool '{name}'. Available tools: {string.Join(", ", all.Keys.OrderBy(k => k, StringComparer.Ordinal))}.", nameof(options));
            active.Add(name);
        }
        return new RuntimeToolSelection(all.Values.ToArray(), active);
    }
}
