using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PiSharp.Extensions;

namespace PiSharp.Runtime;

/// <summary>
/// Runtime factory for extension state stores. One <see cref="ExtensionStateStore"/> per
/// (normalized namespace, scope), rooted at <c>~/.pi/PiSharp/extensions/&lt;ns&gt;</c> (User) and
/// <c>&lt;cwd&gt;/.pi/PiSharp/extensions/&lt;ns&gt;</c> (Project). The namespaces are normalized
/// from extension ids (lowercase, non [a-z0-9-] → '-'), so no path traversal is possible.
/// </summary>
public sealed class ExtensionStateService : IExtensionRuntimeState
{
    private readonly ConcurrentDictionary<(string Namespace, ExtensionStateScope Scope), IExtensionStateStore> _stores = new();
    private readonly string _userRoot;
    private readonly string _projectRoot;
    private readonly ILoggerFactory? _loggerFactory;

    public ExtensionStateService(string userRoot, string projectRoot, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(userRoot)) throw new ArgumentException("User state root is required.", nameof(userRoot));
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new ArgumentException("Project state root is required.", nameof(projectRoot));
        _userRoot = userRoot;
        _projectRoot = projectRoot;
        _loggerFactory = loggerFactory;
    }

    public IExtensionStateStore GetStore(string extensionNamespace, ExtensionStateScope scope)
    {
        if (string.IsNullOrWhiteSpace(extensionNamespace))
            throw new ArgumentException("Extension namespace is required.", nameof(extensionNamespace));
        return _stores.GetOrAdd((extensionNamespace, scope), key =>
        {
            var root = scope == ExtensionStateScope.User
                ? Path.Combine(_userRoot, key.Namespace)
                : Path.Combine(_projectRoot, key.Namespace);
            return new ExtensionStateStore(key.Namespace, key.Scope, root, _loggerFactory);
        });
    }
}
