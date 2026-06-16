namespace PiSharp.Extensions;

public interface IExtension
{
    Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default);
}
