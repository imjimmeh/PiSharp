using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.DaemonCommands.Tests;

/// <summary>
/// Covers the P04 daemon command surface (GAP-55/GAP-56):
/// <c>install_extension</c>, <c>uninstall_extension</c>, <c>update_extension</c>,
/// <c>list_installed_extensions</c>, <c>manage_skill</c>, <c>get_skills</c> and the
/// client-facing <c>extensions_changed</c>/<c>skills_changed</c> event emission.
/// </summary>
public sealed class PackageSkillDaemonCommandTests
{
    [Fact]
    public async Task InstallExtension_InvokesBindingAndEmitsExtensionsChanged()
    {
        var invoked = new List<(string Reference, bool Local, bool Force, bool Offline)>();
        var binding = CreateBinding();
        binding.InstallExtensionAsync = (reference, local, force, offline, _) =>
        {
            invoked.Add((reference, local, force, offline));
            return Task.FromResult(new ExtensionPackageResult(true, Path: reference));
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "i",
            type = ServerCommandTypes.InstallExtension,
            serverSessionId = live.Id,
            reference = "npm:pkg@1",
            local = true,
            force = true,
            offline = true
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ExtensionPackageResult>(response.Data);
        Assert.True(result.Success);
        var call = Assert.Single(invoked);
        Assert.Equal("npm:pkg@1", call.Reference);
        Assert.True(call.Local);
        Assert.True(call.Force);
        Assert.True(call.Offline);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.PackagesChanged));
        var payload = ReadChangedPayload(changed.Event.Data);
        Assert.Equal(["npm:pkg@1"], payload["added"]);
    }

    [Fact]
    public async Task InstallExtension_UnboundBinding_ReturnsFailedPackageResult()
    {
        var (handler, live) = await CreateSessionWithBindingAsync(CreateBinding());

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "i",
            type = ServerCommandTypes.InstallExtension,
            serverSessionId = live.Id,
            reference = "npm:pkg@1"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<ExtensionPackageResult>(response.Data);
        Assert.False(result.Success);
        Assert.Contains("not bound", result.Error);
    }

    [Fact]
    public async Task InstallExtension_WithoutSession_Fails()
    {
        var (handler, _) = await CreateSessionWithBindingAsync(CreateBinding());

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "i",
            type = ServerCommandTypes.InstallExtension,
            reference = "npm:pkg@1"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task UpdateExtension_InvokesBindingAndEmitsExtensionsChanged()
    {
        ExtensionPackageUpdateRequest? received = null;
        var binding = CreateBinding();
        binding.UpdateExtensionAsync = (request, _) =>
        {
            received = request;
            return Task.FromResult(new ExtensionPackageResult(true));
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "u",
            type = ServerCommandTypes.UpdateExtension,
            serverSessionId = live.Id,
            source = "npm:pkg@2",
            extensions = true,
            extensionSource = "ext-a",
            force = true,
            offline = false
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.True(Assert.IsType<ExtensionPackageResult>(response.Data).Success);
        Assert.NotNull(received);
        Assert.Equal("npm:pkg@2", received.Source);
        Assert.True(received.Extensions);
        Assert.Equal("ext-a", received.ExtensionSource);
        Assert.True(received.Force);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.PackagesChanged));
        Assert.Equal(["npm:pkg@2"], ReadChangedPayload(changed.Event.Data)["updated"]);
    }

    [Fact]
    public async Task RemoveExtension_InvokesBindingAndEmitsExtensionsChanged()
    {
        var invoked = new List<(string Reference, bool Local)>();
        var binding = CreateBinding();
        binding.RemoveExtensionAsync = (reference, local, _) =>
        {
            invoked.Add((reference, local));
            return Task.FromResult(true);
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "r",
            type = ServerCommandTypes.RemoveExtension,
            serverSessionId = live.Id,
            reference = "npm:pkg@1",
            local = true
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.True(Assert.IsType<bool>(response.Data));
        var call = Assert.Single(invoked);
        Assert.Equal("npm:pkg@1", call.Reference);
        Assert.True(call.Local);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.PackagesChanged));
        Assert.Equal(["npm:pkg@1"], ReadChangedPayload(changed.Event.Data)["removed"]);
    }

    [Fact]
    public async Task RemoveExtension_WhenBindingReturnsFalse_DoesNotEmitEvent()
    {
        var binding = CreateBinding();
        binding.RemoveExtensionAsync = (_, _, _) => Task.FromResult(false);
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "r",
            type = ServerCommandTypes.RemoveExtension,
            serverSessionId = live.Id,
            reference = "npm:missing@1"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.False(Assert.IsType<bool>(response.Data));
        Assert.Empty(FindEvents(live, ExtensionEventNames.PackagesChanged));
    }

    [Fact]
    public async Task ListInstalledExtensions_ReturnsInstalledPackages()
    {
        var binding = CreateBinding();
        binding.ListInstalledExtensionsAsync = _ => Task.FromResult<IReadOnlyList<ExtensionInstalledPackage>>(
            [new ExtensionInstalledPackage("npm:pkg@1", "global"), new ExtensionInstalledPackage("local-pkg", "local")]);
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "l",
            type = ServerCommandTypes.ListInstalledExtensions,
            serverSessionId = live.Id
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var packages = Assert.IsAssignableFrom<IReadOnlyList<ExtensionInstalledPackage>>(response.Data);
        Assert.Equal(2, packages.Count);
        Assert.Equal("npm:pkg@1", packages[0].Source);
        Assert.Equal("global", packages[0].Layer);
        Assert.Equal("local-pkg", packages[1].Source);
        Assert.Equal("local", packages[1].Layer);
    }

    [Fact]
    public async Task ManageSkill_Create_InvokesBindingAndEmitsSkillsChanged()
    {
        ManagedSkillCreateRequest? received = null;
        var binding = CreateBinding();
        binding.ManagedSkillCreateAsync = (request, _) =>
        {
            received = request;
            return Task.FromResult(new ManagedSkillDescriptor(request.Name, request.Description, request.Content, request.DisableModelInvocation));
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "m",
            type = ServerCommandTypes.ManageSkill,
            serverSessionId = live.Id,
            op = "create",
            name = "my-skill",
            description = "A skill",
            content = "# body",
            disableModelInvocation = true
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var descriptor = Assert.IsType<ManagedSkillDescriptor>(response.Data);
        Assert.Equal("my-skill", descriptor.Name);
        Assert.Equal("A skill", descriptor.Description);
        Assert.Equal("# body", descriptor.Content);
        Assert.True(descriptor.DisableModelInvocation);
        Assert.NotNull(received);
        Assert.Equal("my-skill", received.Name);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.SkillsChanged));
        Assert.Equal(["my-skill"], ReadChangedPayload(changed.Event.Data)["added"]);
    }

    [Fact]
    public async Task ManageSkill_Update_InvokesBindingAndEmitsSkillsChanged()
    {
        var received = new List<(string Name, ManagedSkillUpdateRequest Request)>();
        var binding = CreateBinding();
        binding.ManagedSkillUpdateAsync = (name, request, _) =>
        {
            received.Add((name, request));
            return Task.FromResult(new ManagedSkillDescriptor(name, request.Description ?? "d", request.Content ?? "c"));
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "m",
            type = ServerCommandTypes.ManageSkill,
            serverSessionId = live.Id,
            op = "update",
            name = "my-skill",
            description = "Updated"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var descriptor = Assert.IsType<ManagedSkillDescriptor>(response.Data);
        Assert.Equal("Updated", descriptor.Description);
        var call = Assert.Single(received);
        Assert.Equal("my-skill", call.Name);
        Assert.Equal("Updated", call.Request.Description);
        Assert.Null(call.Request.Content);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.SkillsChanged));
        Assert.Equal(["my-skill"], ReadChangedPayload(changed.Event.Data)["updated"]);
    }

    [Fact]
    public async Task ManageSkill_Delete_InvokesBindingAndEmitsSkillsChanged()
    {
        string? deletedName = null;
        var binding = CreateBinding();
        binding.ManagedSkillDeleteAsync = (name, _) =>
        {
            deletedName = name;
            return Task.FromResult(true);
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "m",
            type = ServerCommandTypes.ManageSkill,
            serverSessionId = live.Id,
            op = "delete",
            name = "my-skill"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.True(Assert.IsType<bool>(response.Data));
        Assert.Equal("my-skill", deletedName);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.SkillsChanged));
        Assert.Equal(["my-skill"], ReadChangedPayload(changed.Event.Data)["removed"]);
    }

    [Fact]
    public async Task ManageSkill_List_ReturnsDescriptors()
    {
        var binding = CreateBinding();
        binding.ManagedSkillListAsync = _ => Task.FromResult<IReadOnlyList<ManagedSkillDescriptor>>(
            [new ManagedSkillDescriptor("s1", "desc", "content", Source: "managed", SourcePriority: 5)]);
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "m",
            type = ServerCommandTypes.ManageSkill,
            serverSessionId = live.Id,
            op = "list"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var skills = Assert.IsAssignableFrom<IReadOnlyList<ManagedSkillDescriptor>>(response.Data);
        var skill = Assert.Single(skills);
        Assert.Equal("s1", skill.Name);
        Assert.Equal("managed", skill.Source);
    }

    [Fact]
    public async Task ManageSkill_Promote_InvokesBindingAndEmitsSkillsChanged()
    {
        string? sourceReference = null;
        var binding = CreateBinding();
        binding.ManagedSkillPromoteAsync = (source, _) =>
        {
            sourceReference = source;
            return Task.FromResult(new ManagedSkillDescriptor("promoted", "d", "c"));
        };
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "m",
            type = ServerCommandTypes.ManageSkill,
            serverSessionId = live.Id,
            op = "promote",
            sourceReference = "skill:learned"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var descriptor = Assert.IsType<ManagedSkillDescriptor>(response.Data);
        Assert.Equal("promoted", descriptor.Name);
        Assert.Equal("skill:learned", sourceReference);
        var changed = Assert.Single(FindEvents(live, ExtensionEventNames.SkillsChanged));
        Assert.Equal(["promoted"], ReadChangedPayload(changed.Event.Data)["added"]);
    }

    [Fact]
    public async Task ManageSkill_UnknownOp_ReturnsFailure()
    {
        var binding = CreateBinding();
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "m",
            type = ServerCommandTypes.ManageSkill,
            serverSessionId = live.Id,
            op = "frobnicate"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("invalid_command", response.Error?.Code);
        Assert.Empty(FindEvents(live, ExtensionEventNames.SkillsChanged));
    }

    [Fact]
    public async Task GetSkills_ReturnsProjectedSkillInfo()
    {
        var binding = CreateBinding();
        binding.GetAllSkillsAsync = _ => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>(
        [
            new ExtensionSkillDefinition("s1", "desc", "body", "/path/SKILL.md",
                DisableModelInvocation: true,
                Globs: ["**/*.md"],
                AlwaysApply: true,
                Hide: true,
                Source: "managed",
                SourcePriority: 5)
        ]);
        var (handler, live) = await CreateSessionWithBindingAsync(binding);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "g",
            type = ServerCommandTypes.GetSkills,
            serverSessionId = live.Id
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var skills = Assert.IsAssignableFrom<IReadOnlyList<ServerSkillInfo>>(response.Data);
        var skill = Assert.Single(skills);
        Assert.Equal("s1", skill.Name);
        Assert.Equal("desc", skill.Description);
        Assert.Equal("managed", skill.Source);
        Assert.Equal(5, skill.SourcePriority);
        Assert.True(skill.Hide);
        Assert.True(skill.AlwaysApply);
        Assert.True(skill.DisableModelInvocation);
        Assert.Equal(["**/*.md"], skill.Globs);
    }

    [Fact]
    public async Task GetSkills_UnboundBinding_ReturnsEmptyList()
    {
        var (handler, live) = await CreateSessionWithBindingAsync(CreateBinding());

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "g",
            type = ServerCommandTypes.GetSkills,
            serverSessionId = live.Id
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ServerSkillInfo>>(response.Data));
    }

    // --- helpers ---

    private static IEnumerable<ServerEventEnvelope> FindEvents(LiveServerSession live, string type)
        => live.EventLog.ReplayFrom(0).Events.Where(envelope => envelope.Event.Type == type);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadChangedPayload(object? data)
    {
        var json = JsonSerializer.Serialize(data, ServerJsonSerializer.Options);
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.EnumerateArray().Select(element => element.GetString() ?? string.Empty).ToArray();
        }
        return result;
    }

    private static ExtensionRuntimeBinding CreateBinding()
        => new(Path.GetTempPath(), false, NoExtensionUi.Instance);

    private static async Task<(PiServerWebSocketHandler Handler, LiveServerSession Live)> CreateSessionWithBindingAsync(ExtensionRuntimeBinding binding)
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd, binding));
        var handler = new PiServerWebSocketHandler(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);
        var cwd = TempRoot();
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(response.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        return (handler, live!);
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root, ExtensionRuntimeBinding binding)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(
            repo,
            createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])),
            initial,
            extensionBinding: binding);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-daemon-commands-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
