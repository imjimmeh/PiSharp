using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Ast.Hash;

/// <summary>
/// Content hashing helpers. Both the <c>ast_edit</c> <c>ContentHash</c> and the anchored edit's
/// per-line block hashes are computed over LF-normalized text so that CRLF/LF files hash
/// identically regardless of the host's line-ending convention.
/// </summary>
public static class ContentHasher
{
    /// <summary>Number of hex characters used for a rendered hashline anchor.</summary>
    public const int AnchorHexLength = 12;

    /// <summary>
    /// SHA-256 hex digest (lowercase, 64 chars) of the given text. Lowercased so output is
    /// stable across platforms.
    /// </summary>
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Returns the first <see cref="AnchorHexLength"/> hex characters of the SHA-256 digest of
    /// <paramref name="text"/>, used as a rendered hashline anchor (<c>@&lt;12-hex&gt;</c>).
    /// </summary>
    public static string Anchor(string text) => Sha256Hex(text)[..AnchorHexLength];
}
