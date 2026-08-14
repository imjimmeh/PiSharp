using PiSharp.Agent.Core.Tools;

namespace PiSharp.Extensions;

/// <summary>
/// Runtime-wide registry of <see cref="IFileContentExtractor"/>s. The runtime
/// owns a single instance, injects it into read-tool construction, and exposes
/// it to extensions through <see cref="IExtensionApi.Files"/>.
/// </summary>
public sealed class FileContentExtractorRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IFileContentExtractor> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IFileContentExtractor> _ordered = [];

    /// <summary>
    /// Registers an extractor under its <see cref="IFileContentExtractor.Id"/>.
    /// Duplicate id throws <see cref="InvalidOperationException"/> unless
    /// <paramref name="overrideExisting"/> is true. An override replaces the
    /// extractor in place, preserving its original registration position.
    /// </summary>
    public void Register(IFileContentExtractor extractor, bool overrideExisting = false)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        if (string.IsNullOrWhiteSpace(extractor.Id))
            throw new ArgumentException("Content extractor id must not be empty.", nameof(extractor));
        lock (_gate)
        {
            if (!overrideExisting && _byId.ContainsKey(extractor.Id))
                throw new InvalidOperationException($"Content extractor '{extractor.Id}' is already registered.");
            if (_byId.ContainsKey(extractor.Id))
            {
                for (var i = 0; i < _ordered.Count; i++)
                    if (string.Equals(_ordered[i].Id, extractor.Id, StringComparison.OrdinalIgnoreCase))
                        _ordered[i] = extractor;
            }
            else
            {
                _ordered.Add(extractor);
            }
            _byId[extractor.Id] = extractor;
        }
    }

    /// <summary>Removes the extractor with <paramref name="id"/>; returns false when none was registered.</summary>
    public bool Unregister(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            if (!_byId.Remove(id)) return false;
            _ordered.RemoveAll(extractor => string.Equals(extractor.Id, id, StringComparison.OrdinalIgnoreCase));
            return true;
        }
    }

    /// <summary>Registered extractors in registration order; the read tool consults the first match.</summary>
    public IReadOnlyList<IFileContentExtractor> Extractors
    {
        get { lock (_gate) return _ordered.ToArray(); }
    }
}
