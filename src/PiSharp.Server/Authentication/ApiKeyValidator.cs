using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Server.Authentication;

/// <summary>
/// Validates the API key for HTTP/WebSocket handshakes. Query-string tokens are accepted only for browser WebSocket clients that cannot set Authorization headers; deployment logs must treat request URLs as sensitive.
/// </summary>
public sealed class ApiKeyValidator(ApiKeyOptions options)
{
    private const string BearerPrefix = "Bearer ";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    private string ExpectedKey => options.ApiKey;

    public bool Validate(HttpContext context)
    {
        var expected = ExpectedKey;
        if (string.IsNullOrWhiteSpace(expected)) return false;

        var provided = ReadBearer(context) ?? ReadQueryToken(context);
        return !string.IsNullOrWhiteSpace(provided) && FixedTimeEquals(provided, expected);
    }

    private static string? ReadBearer(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? value[BearerPrefix.Length..].Trim() : null;
    }

    private static string? ReadQueryToken(HttpContext context)
        => context.Request.Query.TryGetValue("access_token", out var value) ? value.ToString() : null;

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
