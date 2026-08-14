using PiSharp.Abstractions.Environment;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Bash;
using PiSharp.Tools.Edit;
using PiSharp.Tools.Files;
using PiSharp.Tools.Search;

namespace PiSharp.Tools;

public static class BuiltInTools
{
    public static IReadOnlyDictionary<string, IAgentTool> CreateAll(IExecutionEnv env, ToolsOptions? options = null, PiSharp.Extensions.InternalUrlRegistry? urlRegistry = null, PiSharp.Extensions.FileContentExtractorRegistry? contentExtractors = null)
        => ToDictionary([
            new ReadTool(env, urlRegistry: urlRegistry, contentExtractors: contentExtractors),
            new BashTool(env),
            new EditTool(env),
            new WriteTool(env),
            new GrepTool(env),
            new FindTool(env),
            new LsTool(env)
        ]);

    public static IReadOnlyDictionary<string, IAgentTool> CreateReadOnly(IExecutionEnv env, ToolsOptions? options = null, PiSharp.Extensions.InternalUrlRegistry? urlRegistry = null, PiSharp.Extensions.FileContentExtractorRegistry? contentExtractors = null)
        => ToDictionary([
            new ReadTool(env, urlRegistry: urlRegistry, contentExtractors: contentExtractors),
            new GrepTool(env),
            new FindTool(env),
            new LsTool(env)
        ]);

    public static IAgentTool CreateTool(string name, IExecutionEnv env, ToolsOptions? options = null, PiSharp.Extensions.InternalUrlRegistry? urlRegistry = null, PiSharp.Extensions.FileContentExtractorRegistry? contentExtractors = null)
        => name switch
        {
            "read" => new ReadTool(env, urlRegistry: urlRegistry, contentExtractors: contentExtractors),
            "bash" => new BashTool(env),
            "edit" => new EditTool(env),
            "write" => new WriteTool(env),
            "grep" => new GrepTool(env),
            "find" => new FindTool(env),
            "ls" => new LsTool(env),
            _ => throw new ArgumentException($"Unknown built-in tool: {name}", nameof(name))
        };


    private static IReadOnlyDictionary<string, IAgentTool> ToDictionary(IEnumerable<IAgentTool> tools)
        => tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
}
