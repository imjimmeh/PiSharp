namespace PiSharp.Eval.Kernels;

/// <summary>
/// Factory for a concrete kernel. Companion kernel plugins register themselves through
/// <see cref="EvalKernelRegistry.RegisterFactory"/> from their own
/// <c>IExtension.InitializeAsync</c>; a new kernel can be added without touching
/// <c>PiSharp.Eval</c>.
/// </summary>
public interface IKernelFactory
{
    string KernelName { get; }
    IKernel Create();
}

/// <summary>
/// Static, in-process registry of kernel factories (the boring cross-plugin mechanism that
/// works inside the collectible plugin host). A future native service bus can replace it
/// without changing the kernel contract.
/// </summary>
public static class EvalKernelRegistry
{
    private static readonly List<IKernelFactory> FactoriesList = [];
    private static readonly object Gate = new();

    public static void RegisterFactory(IKernelFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(factory.KernelName))
            throw new ArgumentException("Kernel factory must declare a non-empty KernelName.", nameof(factory));
        lock (Gate)
        {
            if (FactoriesList.Any(f => string.Equals(f.KernelName, factory.KernelName, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Eval kernel factory '{factory.KernelName}' is already registered.");
            FactoriesList.Add(factory);
        }
    }

    public static IReadOnlyList<IKernelFactory> Factories
    {
        get { lock (Gate) return FactoriesList.ToArray(); }
    }

    public static IKernelFactory? FindFactory(string kernelName)
    {
        lock (Gate) return FactoriesList.FirstOrDefault(f => string.Equals(f.KernelName, kernelName, StringComparison.Ordinal));
    }

    /// <summary>Test/teardown helper: clears all registered factories.</summary>
    public static void Clear()
    {
        lock (Gate) FactoriesList.Clear();
    }
}
