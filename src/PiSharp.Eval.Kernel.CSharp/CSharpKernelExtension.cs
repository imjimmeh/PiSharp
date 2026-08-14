using PiSharp.Eval.Kernels;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-eval-kernel-csharp", Name = "PiSharp Eval C# Kernel", Version = "1.0.0")]

namespace PiSharp.Eval.Kernel.CSharp;

/// <summary>
/// <c>pisharp-eval-kernel-csharp</c> extension entry: registers the in-process Roslyn
/// C# scripting kernel factory with <see cref="EvalKernelRegistry"/> so the
/// <c>eval</c> tool can start a <c>"csharp"</c> kernel. No other surface is needed —
/// the core <c>PiSharp.Eval</c> plugin owns the registry, tools, and commands.
/// </summary>
public sealed class CSharpKernelExtension : IExtension
{
    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        EvalKernelRegistry.RegisterFactory(new CSharpKernelFactory());
        return Task.CompletedTask;
    }
}
