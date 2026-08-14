using PiSharp.Agent.Core.Tools;

namespace PiSharp.Extensions;

/// <summary>
/// Registration surface for file-content extractors exposed to extensions via
/// <see cref="IExtensionApi.Files"/>. Wraps the runtime-wide
/// <see cref="FileContentExtractorRegistry"/>.
/// </summary>
public interface IExtensionFileApi
{
    /// <summary>
    /// Registers a content extractor in the runtime-wide registry. Duplicate id
    /// throws unless <paramref name="overrideExisting"/>. The returned
    /// <see cref="IDisposable"/> unregisters the extractor when disposed.
    /// </summary>
    IDisposable RegisterContentExtractor(IFileContentExtractor extractor, bool overrideExisting = false);

    /// <summary>Registered extractors in registration order.</summary>
    IReadOnlyList<IFileContentExtractor> ContentExtractors { get; }
}
