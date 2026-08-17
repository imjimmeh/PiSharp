namespace PiSharp.Abstractions.Exceptions;

/// <summary>
/// Root base exception for all PiSharp domain errors.
/// </summary>
public class PiSharpException : Exception
{
    public PiSharpException() { }
    public PiSharpException(string message) : base(message) { }
    public PiSharpException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown during session persistence, branch navigation, or session lifecycle operations.
/// </summary>
public class SessionException : PiSharpException
{
    public string? SessionId { get; }

    public SessionException(string message, string? sessionId = null) : base(message)
    {
        SessionId = sessionId;
    }

    public SessionException(string message, Exception innerException, string? sessionId = null) : base(message, innerException)
    {
        SessionId = sessionId;
    }
}

/// <summary>
/// Exception thrown when an AI model provider call fails, times out, or returns invalid payloads.
/// </summary>
public class ProviderException : PiSharpException
{
    public string? Provider { get; }
    public string? Model { get; }

    public ProviderException(string message, string? provider = null, string? model = null) : base(message)
    {
        Provider = provider;
        Model = model;
    }

    public ProviderException(string message, Exception innerException, string? provider = null, string? model = null) : base(message, innerException)
    {
        Provider = provider;
        Model = model;
    }
}

/// <summary>
/// Exception thrown when extension loading, registration, or execution fails.
/// </summary>
public class ExtensionException : PiSharpException
{
    public string? ExtensionId { get; }

    public ExtensionException(string message, string? extensionId = null) : base(message)
    {
        ExtensionId = extensionId;
    }

    public ExtensionException(string message, Exception innerException, string? extensionId = null) : base(message, innerException)
    {
        ExtensionId = extensionId;
    }
}

/// <summary>
/// Exception thrown when TypeScript bridge communication, RPC dispatch, or process IPC fails.
/// </summary>
public class BridgeException : PiSharpException
{
    public BridgeException(string message) : base(message) { }
    public BridgeException(string message, Exception innerException) : base(message, innerException) { }
}
