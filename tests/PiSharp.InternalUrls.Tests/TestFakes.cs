using System.Text.Json;
using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Abstractions.Messages;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Minimal <see cref="IExtensionApi"/> host for <see cref="InternalUrlsExtension"/>
/// tests: only <see cref="IExtensionApi.Urls"/> (backed by a real
/// <see cref="InternalUrlRegistry"/>) and <see cref="IExtensionApi.Skills"/>
/// (in-memory) are wired; every other surface throws <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class FakeExtensionApi : IExtensionApi
{
    public FakeExtensionApi()
    {
        Registry = new InternalUrlRegistry();
        Skills = new FakeSkillApi();
    }

    public InternalUrlRegistry Registry { get; }

    public IExtensionUrlApi Urls => new FakeUrlApi(Registry);

    public FakeSkillApi Skills { get; }

    IExtensionSkillApi IExtensionApi.Skills => Skills;

    public ExtensionDescriptor Descriptor => new("pisharp-internal-urls", "PiSharp Internal URL Schemes", "0.1.0");

    public string Cwd => @"C:\test-cwd";

    public bool HasUi => false;

    public IExecutionEnv? ExecutionEnv => null;

    public IExtensionUi Ui => throw new NotSupportedException();
    public IExtensionSessionApi Session => throw new NotSupportedException();
    public IExtensionToolApi Tools => throw new NotSupportedException();
    public IExtensionModelApi Model => throw new NotSupportedException();
    public IExtensionEventBus Events => throw new NotSupportedException();
    public IExtensionPromptApi Prompt => throw new NotSupportedException();
    public IExtensionSettingsApi Settings => throw new NotSupportedException();
    public IExtensionStateApi State => throw new NotSupportedException();

    public IDisposable On(string eventName, ExtensionEventHandler handler) => throw new NotSupportedException();
    public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException();
    public IDisposable RegisterTool(ExtensionToolRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException();
    public IDisposable RegisterCommand(ExtensionCommandRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException();
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
    public bool RemoveProvider(string api) => throw new NotSupportedException();
    public object? GetFlag(string name) => throw new NotSupportedException();
    public IReadOnlyDictionary<string, object?> GetFlags() => throw new NotSupportedException();
    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    private sealed class FakeUrlApi(InternalUrlRegistry registry) : IExtensionUrlApi
    {
        public void RegisterResolver(IInternalUrlResolver resolver, bool overrideExisting = false)
            => registry.Register(resolver, overrideExisting);

        public IReadOnlyList<string> Schemes => registry.Schemes;
    }

    internal sealed class FakeSkillApi : IExtensionSkillApi
    {
        private readonly Dictionary<string, ExtensionSkillDefinition> _skills = new(StringComparer.Ordinal);

        public IDisposable RegisterSkill(ExtensionSkillDefinition registration)
        {
            _skills[registration.Name] = registration;
            return new DisposableAction(() => _skills.Remove(registration.Name));
        }

        public Task<IReadOnlyList<ExtensionSkillDefinition>> GetAllSkillsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>(_skills.Values.ToArray());

        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class DisposableAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>
/// In-memory <see cref="IExecutionEnv"/> that serves skill asset files from a
/// dictionary. Only the members the resolvers exercise are functional.
/// </summary>
internal sealed class FakeExecutionEnv : IExecutionEnv
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeExecutionEnv(IEnumerable<KeyValuePair<string, string>>? files = null)
    {
        if (files is not null)
        {
            foreach (var (path, content) in files) _files[path] = content;
        }
    }

    public string Cwd => @"C:\test-cwd";

    public Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_files.TryGetValue(path, out var content))
            return Task.FromResult(Result<string, FileError>.Ok(content));
        return Task.FromResult(Result<string, FileError>.Err(new FileError(FileErrorCode.NotFound, $"File not found: {path}", path)));
    }

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<PiSharp.Abstractions.Environment.FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<IReadOnlyList<PiSharp.Abstractions.Environment.FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
