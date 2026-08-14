using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai;
using PiSharp.Ai.Models;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

[Collection("GlobalModelRegistry")]
public sealed class RuntimeModelSelectorRoleTests : IDisposable

{
    public RuntimeModelSelectorRoleTests()
    {
        ModelRegistry.ResetToBuiltIns();
        ModelRoleRegistry.Clear();
    }

    public void Dispose()
    {
        ModelRegistry.ResetToBuiltIns();
        ModelRoleRegistry.Clear();
    }

    // ---- registry behaviour ----

    [Fact]
    public void Registry_FirstWins_ByRegistrationOrder()
    {
        ModelRoleRegistry.Register(new FakeRoleResolver("a", ("role-x", new ModelRoleResolution("role-x", ["prov/model-a"], null))));
        ModelRoleRegistry.Register(new FakeRoleResolver("b", ("role-x", new ModelRoleResolution("role-x", ["prov/model-b"], null))));

        var resolution = ModelRoleRegistry.Resolve("role-x");
        Assert.NotNull(resolution);
        Assert.Equal(["prov/model-a"], resolution.Selectors);
    }

    [Fact]
    public void Registry_Unregister_RemovesResolversForSource()
    {
        ModelRoleRegistry.Register(new FakeRoleResolver("a", ("role-x", new ModelRoleResolution("role-x", ["prov/model-a"], null))));
        var removed = ModelRoleRegistry.Unregister("a");
        Assert.True(removed);
        Assert.Null(ModelRoleRegistry.Resolve("role-x"));
        Assert.False(ModelRoleRegistry.Unregister("a"));
    }

    [Fact]
    public void Registry_Clear_EmptiesAllResolvers()
    {
        ModelRoleRegistry.Register(new FakeRoleResolver("a", ("role-x", new ModelRoleResolution("role-x", ["prov/model-a"], null))));
        ModelRoleRegistry.Clear();
        Assert.Empty(ModelRoleRegistry.Roles);
        Assert.Null(ModelRoleRegistry.Resolve("role-x"));
    }

    [Fact]
    public void Registry_Roles_UnionsAcrossResolvers()
    {
        ModelRoleRegistry.Register(new FakeRoleResolver("a", ("role-a", new ModelRoleResolution("role-a", ["prov/model-a"], null))));
        ModelRoleRegistry.Register(new FakeRoleResolver("b", ("role-b", new ModelRoleResolution("role-b", ["prov/model-b"], null))));
        var roles = ModelRoleRegistry.Roles;
        Assert.Contains("role-a", roles);
        Assert.Contains("role-b", roles);
    }

    // ---- @role expansion ----

    [Fact]
    public void AtRole_ResolvesViaRegisteredResolver()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        ModelRoleRegistry.Register(new FakeRoleResolver("test", ("fast_worker", new ModelRoleResolution("fast_worker", ["prov/haiku"], null))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@fast_worker", null));
        Assert.Equal("prov", result.Model.Provider);
        Assert.Equal("haiku", result.Model.Id);
        Assert.Equal(ThinkingLevel.Off, result.ThinkingLevel);
    }

    [Fact]
    public void AtRole_PrioritizedArray_PicksFirstResolvableCandidate()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        ModelRoleRegistry.Register(new FakeRoleResolver("test", ("smol", new ModelRoleResolution("smol", ["prov/missing", "prov/haiku"], null))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@smol", null));
        Assert.Equal("prov", result.Model.Provider);
        Assert.Equal("haiku", result.Model.Id);
    }

    [Fact]
    public void AtRole_SelectorThinkingSuffix_SetsRequestedThinking()
    {
        RegisterModel("prov", "sonnet", reasoning: true, budgets: new Dictionary<string, int> { ["low"] = 1000 });
        ModelRoleRegistry.Register(new FakeRoleResolver("test", ("review", new ModelRoleResolution("review", ["prov/sonnet:low"], null))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@review", null));
        Assert.Equal(ThinkingLevel.Low, result.ThinkingLevel);
    }

    [Fact]
    public void AtRole_RequestSuffixThinking_ComposesWithRole()
    {
        RegisterModel("prov", "haiku", reasoning: true, budgets: new Dictionary<string, int> { ["high"] = 1000 });
        ModelRoleRegistry.Register(new FakeRoleResolver("test", ("worker", new ModelRoleResolution("worker", ["prov/haiku"], null))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@worker:high", null));
        Assert.Equal(ThinkingLevel.High, result.ThinkingLevel);
    }

    [Fact]
    public void AtRole_UnknownRole_Throws_WithoutFallingBack()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@nonexistent", null)));
        Assert.Contains("Unknown model role", exception.Message);
    }

    [Fact]
    public void AtRole_EffortThinking_FoldsBelowSuffixAndRequest()
    {
        // Model supports both low and high so clamping does not obscure precedence.
        RegisterModel("prov", "haiku", reasoning: true, budgets: new Dictionary<string, int> { ["low"] = 1000, ["high"] = 1000 });
        ModelRoleRegistry.Register(new FakeRoleResolver("test",
            ("worker", new ModelRoleResolution("worker", ["prov/haiku"], new EffortPreset(ThinkingLevel.Low, null)))));

        // request.Thinking wins over effort.
        var withRequest = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@worker", ThinkingLevel.High));
        Assert.Equal(ThinkingLevel.High, withRequest.ThinkingLevel);

        // effort preset supplies the thinking when nothing more explicit given.
        var withEffortOnly = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@worker", null));
        Assert.Equal(ThinkingLevel.Low, withEffortOnly.ThinkingLevel);
    }
    [Fact]
    public void AtRole_NoResolvers_ThrowsDocumentedError()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@anything", null)));
        Assert.Contains("Unknown model role", exception.Message);
    }

    [Fact]
    public void AtRole_NestedRoles_Resolve()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        ModelRoleRegistry.Register(new FakeRoleResolver("test",
            ("a", new ModelRoleResolution("a", ["@b"], null)),
            ("b", new ModelRoleResolution("b", ["prov/haiku"], null))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@a", null));
        Assert.Equal("haiku", result.Model.Id);
    }

    [Fact]
    public void AtRole_Cycle_Throws()
    {
        ModelRoleRegistry.Register(new FakeRoleResolver("test",
            ("a", new ModelRoleResolution("a", ["@b"], null)),
            ("b", new ModelRoleResolution("b", ["@a"], null))));
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@a", null)));
    }

    [Fact]
    public void AtRole_DepthExceedingMax_Throws()
    {
        var resolver = new FakeRoleResolver("test");
        for (var i = 0; i < 10; i++)
        {
            var next = i < 9 ? $"@r{i + 1}" : "prov/haiku";
            resolver.AddRole($"r{i}", new ModelRoleResolution($"r{i}", [next], null));
        }
        ModelRoleRegistry.Register(resolver);
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@r0", null)));
    }

    [Fact]
    public void NonAtRequest_BehavesAsBefore()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest("prov", "haiku", null));
        Assert.Equal("prov", result.Model.Provider);
        Assert.Equal("haiku", result.Model.Id);
    }

    // ---- effort folding & budget merge ----


    [Fact]
    public void AtRole_BudgetOverride_AddsSupportedLevel_ClampHonorsIt()
    {
        // Model natively supports only "low".
        RegisterModel("prov", "sonnet", reasoning: true, budgets: new Dictionary<string, int> { ["low"] = 1000 });
        // Role effort adds a "high" budget and requests high thinking.
        ModelRoleRegistry.Register(new FakeRoleResolver("test",
            ("review", new ModelRoleResolution("review", ["prov/sonnet"], new EffortPreset(ThinkingLevel.High, new Dictionary<string, int> { ["high"] = 24000 })))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@review", null));
        // The budget override merged "high" into the model's map, so clamping keeps High.
        Assert.Equal(ThinkingLevel.High, result.ThinkingLevel);
    }

    [Fact]
    public void AtRole_BudgetMissingName_ReturnsDescriptorUnchangedByDefault()
    {
        RegisterModel("prov", "haiku", reasoning: false);
        ModelRoleRegistry.Register(new FakeRoleResolver("test",
            ("worker", new ModelRoleResolution("worker", ["prov/haiku"], new EffortPreset(null, null)))));

        var result = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@worker", null));
        Assert.Equal(ThinkingLevel.Off, result.ThinkingLevel);
    }

    private static void RegisterModel(string provider, string id, bool reasoning, IReadOnlyDictionary<string, int>? budgets = null)
    {
        var descriptor = new ModelDescriptor(provider, id, "test-api", Reasoning: reasoning, ThinkingLevelMap: budgets);
        ModelRegistry.RegisterModel(new CatalogModel(provider, id, descriptor), "role-tests");
    }

    private sealed class FakeRoleResolver : IModelRoleResolver
    {
        private readonly Dictionary<string, ModelRoleResolution> _roles = new(StringComparer.Ordinal);

        public FakeRoleResolver(string sourceId, params (string Role, ModelRoleResolution Resolution)[] roles)
        {
            SourceId = sourceId;
            foreach (var (role, resolution) in roles) _roles[role] = resolution;
        }

        public string SourceId { get; }

        public void AddRole(string role, ModelRoleResolution resolution) => _roles[role] = resolution;

        public ModelRoleResolution? Resolve(string role)
            => _roles.TryGetValue(role, out var resolution) ? resolution : null;

        public IReadOnlyList<string> Roles => _roles.Keys.ToArray();
    }
}
