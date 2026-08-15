using PiSharp.Abstractions.Environment;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// The F5 binding-complete gate: a binding handed to any extension before its core
/// capabilities (ExecutionEnv, SendMessageAsync, ExecuteToolByNameAsync) are wired must fail
/// loudly instead of silently no-oping.
/// </summary>
public sealed class ExtensionBindingCompletionTests
{
    private static ExtensionRuntimeBinding Fresh()
        => new("C:/project", false, NoExtensionUi.Instance);

    private static void WireAll(ExtensionRuntimeBinding binding)
    {
        binding.ExecutionEnv = new HostlessExecutionEnv("C:/project");
        binding.SendMessageAsync = (_, _, _, _) => Task.CompletedTask;
        binding.ExecuteToolByNameAsync = (_, _, _) => Task.FromResult(new AgentToolResult<object?>([], null));
    }

    [Fact]
    public void BindingsComplete_AllCoreWired_DoesNotThrow()
    {
        var binding = Fresh();
        WireAll(binding);

        binding.BindingsComplete();
    }

    [Fact]
    public void BindingsComplete_MissingExecutionEnv_Throws()
    {
        var binding = Fresh();
        binding.SendMessageAsync = (_, _, _, _) => Task.CompletedTask;
        binding.ExecuteToolByNameAsync = (_, _, _) => Task.FromResult(new AgentToolResult<object?>([], null));

        var error = Assert.Throws<InvalidOperationException>(() => binding.BindingsComplete());
        Assert.Contains("ExecutionEnv", error.Message);
    }

    [Fact]
    public void BindingsComplete_DefaultSendMessage_Throws()
    {
        var binding = Fresh();
        binding.ExecutionEnv = new HostlessExecutionEnv("C:/project");
        // SendMessageAsync left on its no-op default.
        binding.ExecuteToolByNameAsync = (_, _, _) => Task.FromResult(new AgentToolResult<object?>([], null));

        var error = Assert.Throws<InvalidOperationException>(() => binding.BindingsComplete());
        Assert.Contains("SendMessageAsync", error.Message);
    }

    [Fact]
    public void BindingsComplete_DefaultExecuteToolByName_Throws()
    {
        var binding = Fresh();
        binding.ExecutionEnv = new HostlessExecutionEnv("C:/project");
        binding.SendMessageAsync = (_, _, _, _) => Task.CompletedTask;
        // ExecuteToolByNameAsync left on its no-op default.

        var error = Assert.Throws<InvalidOperationException>(() => binding.BindingsComplete());
        Assert.Contains("ExecuteToolByNameAsync", error.Message);
    }

    [Fact]
    public void ValidateBound_ListsAllMissingWhenNothingWired()
    {
        var binding = Fresh();

        var error = Assert.Throws<InvalidOperationException>(() => binding.ValidateBound());
        Assert.Contains("ExecutionEnv", error.Message);
        Assert.Contains("SendMessageAsync", error.Message);
        Assert.Contains("ExecuteToolByNameAsync", error.Message);
    }
}
