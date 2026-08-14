namespace PiSharp.Extensions;

/// <summary>
/// Registration surface for <see cref="IStreamDeltaInterceptor"/> instances (P10
/// time-traveling stream rules). The host registry is authoritative; the returned
/// <see cref="IDisposable"/> unregisters the interceptor.
/// </summary>
public interface IStreamDeltaInterceptorApi
{
    IDisposable RegisterInterceptor(IStreamDeltaInterceptor interceptor);
}
