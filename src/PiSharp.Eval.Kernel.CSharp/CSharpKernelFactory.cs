using PiSharp.Eval.Kernels;

namespace PiSharp.Eval.Kernel.CSharp;

/// <summary>
/// Factory for the in-process Roslyn C# scripting kernel.
/// </summary>
public sealed class CSharpKernelFactory : IKernelFactory
{
    public string KernelName => CSharpKernel.KernelName;

    public IKernel Create() => new CSharpKernel();
}
