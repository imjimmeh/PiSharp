namespace PiSharp.Extensions;

/// <summary>
/// Mirrors <see cref="global::PiSharp.Agent.Resources.Theme.TuiColorSchemeDocument"/>
/// field-for-field so the same JSON deserializes into either.
/// </summary>
public sealed record ExtensionThemeColorScheme(
    string? NormalForeground = null,
    string? NormalBackground = null,
    string? FocusForeground = null,
    string? FocusBackground = null,
    string? HotNormalForeground = null,
    string? HotNormalBackground = null,
    string? HotFocusForeground = null,
    string? HotFocusBackground = null,
    string? DisabledForeground = null,
    string? DisabledBackground = null);

/// <summary>
/// Mirrors <see cref="global::PiSharp.Agent.Resources.Theme.TuiThemeDocument"/>
/// field-for-fields so the same JSON deserializes into either.
/// </summary>
public sealed record ExtensionThemeDocument(
    string Name,
    IReadOnlyDictionary<string, string>? Tokens = null,
    ExtensionThemeColorScheme? Default = null,
    ExtensionThemeColorScheme? Dialog = null,
    ExtensionThemeColorScheme? Menu = null);

/// <summary>
/// Lightweight theme reference; the <see cref="Document"/> is populated
/// for <c>get_theme</c> / <c>get_all_themes</c> and may be null for
/// <c>list_themes</c> (names only).
/// </summary>
public sealed record ExtensionThemeInfo(string Name, ExtensionThemeDocument? Document = null);
