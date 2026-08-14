namespace PiSharp.Sdk;

/// <summary>Raised for SDK-level failures: no compatible daemon, daemon not reachable, or a failed command.</summary>
public sealed class SdkException(string message, Exception? innerException = null) : Exception(message, innerException);
