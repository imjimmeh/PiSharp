using PiSharp.Agent.Resources.Theme;
using PiSharp.Tui.Interactive.Rendering;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Theme;

public enum TuiThemeToken
{
    Accent,
    Border,
    BorderAccent,
    BorderMuted,
    Muted,
    Dim,
    Text,
    Success,
    Error,
    Warning,
    SelectedBackground,
    PageBackground,
    UserMessageBackground,
    UserMessageText,
    CustomMessageBackground,
    CustomMessageText,
    ToolPendingBackground,
    ToolSuccessBackground,
    ToolErrorBackground,
    ToolOutput,
    MarkdownHeading,
    MarkdownLink,
    MarkdownLinkUrl,
    MarkdownCode,
    MarkdownCodeBlock,
    MarkdownQuote,
    MarkdownHorizontalRule,
    MarkdownListBullet,
    ToolDiffAdded,
    ToolDiffRemoved,
    ToolDiffContext,
    ThinkingText,
    ThinkingOff,
    ThinkingMinimal,
    ThinkingLow,
    ThinkingMedium,
    ThinkingHigh,
    ThinkingXHigh
}

public static class TuiTheme
{
    public const string DefaultThemeName = "Dark";
    // Legacy constants are kept for callers that still reference static color names.
    public const string Accent = "#8abeb7";
    public const string Border = "#5f87ff";
    public const string BorderAccent = "#00d7ff";
    public const string Muted = "#808080";
    public const string Dim = "#666666";
    public const string Text = "#d4d4d4";
    public const string Error = "#cc6666";
    public const string Success = "#b5bd68";
    public const string Warning = "#ffff00";
    public const string SelectedBg = "#3a3a4a";
    public const string PageBg = "#18181e";
    public const string BorderMuted = "#505050";
    public const string UserMessageBg = "#343541";
    public const string CustomMessageBg = "#2d2838";
    public const string ToolPendingBg = "#282832";
    public const string ToolSuccessBg = "#283228";
    public const string ToolErrorBg = "#3c2828";

    public static ColorScheme DefaultColorScheme { get; } = new()
    {
        Normal = new Terminal.Gui.Attribute(ColorName16.Gray, ColorName16.Black),
        Focus = new Terminal.Gui.Attribute(ColorName16.BrightCyan, ColorName16.Black),
        HotNormal = new Terminal.Gui.Attribute(ColorName16.Cyan, ColorName16.Black),
        HotFocus = new Terminal.Gui.Attribute(ColorName16.BrightCyan, ColorName16.Black),
        Disabled = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black)
    };

    public static ColorScheme PopupColorScheme { get; } = new()
    {
        Normal = new Terminal.Gui.Attribute(ColorName16.Gray, ColorName16.Black),
        Focus = new Terminal.Gui.Attribute(ColorName16.BrightCyan, ColorName16.Blue),
        HotNormal = new Terminal.Gui.Attribute(ColorName16.Cyan, ColorName16.Black),
        HotFocus = new Terminal.Gui.Attribute(ColorName16.BrightCyan, ColorName16.Blue),
        Disabled = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black)
    };

    public static ColorScheme PromptBorderColorScheme { get; } = new()
    {
        Normal = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black),
        Focus = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black),
        HotNormal = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black),
        HotFocus = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black),
        Disabled = new Terminal.Gui.Attribute(ColorName16.DarkGray, ColorName16.Black)
    };

    private static readonly IReadOnlyDictionary<TuiThemeToken, string> TokenHexPalette = new Dictionary<TuiThemeToken, string>
    {
        [TuiThemeToken.Accent] = "#8abeb7",
        [TuiThemeToken.Border] = "#5f87ff",
        [TuiThemeToken.BorderAccent] = "#00d7ff",
        [TuiThemeToken.BorderMuted] = BorderMuted,
        [TuiThemeToken.Muted] = Muted,
        [TuiThemeToken.Dim] = Dim,
        [TuiThemeToken.Text] = Text,
        [TuiThemeToken.Success] = Success,
        [TuiThemeToken.Error] = Error,
        [TuiThemeToken.Warning] = Warning,
        [TuiThemeToken.SelectedBackground] = SelectedBg,
        [TuiThemeToken.PageBackground] = PageBg,
        [TuiThemeToken.UserMessageBackground] = UserMessageBg,
        [TuiThemeToken.UserMessageText] = Text,
        [TuiThemeToken.CustomMessageBackground] = CustomMessageBg,
        [TuiThemeToken.CustomMessageText] = Text,
        [TuiThemeToken.ToolPendingBackground] = ToolPendingBg,
        [TuiThemeToken.ToolSuccessBackground] = ToolSuccessBg,
        [TuiThemeToken.ToolErrorBackground] = ToolErrorBg,
        [TuiThemeToken.ToolOutput] = Muted,
        [TuiThemeToken.MarkdownHeading] = "#f0c674",
        [TuiThemeToken.MarkdownLink] = "#81a2be",
        [TuiThemeToken.MarkdownLinkUrl] = "#666666",
        [TuiThemeToken.MarkdownCode] = Accent,
        [TuiThemeToken.MarkdownCodeBlock] = Success,
        [TuiThemeToken.MarkdownQuote] = Muted,
        [TuiThemeToken.MarkdownHorizontalRule] = Muted,
        [TuiThemeToken.MarkdownListBullet] = Accent,
        [TuiThemeToken.ToolDiffAdded] = Success,
        [TuiThemeToken.ToolDiffRemoved] = Error,
        [TuiThemeToken.ToolDiffContext] = Muted,
        [TuiThemeToken.ThinkingText] = Muted,
        [TuiThemeToken.ThinkingOff] = BorderMuted,
        [TuiThemeToken.ThinkingMinimal] = "#6e6e6e",
        [TuiThemeToken.ThinkingLow] = "#5f87af",
        [TuiThemeToken.ThinkingMedium] = "#81a2be",
        [TuiThemeToken.ThinkingHigh] = "#b294bb",
        [TuiThemeToken.ThinkingXHigh] = "#d183e8"
    };

    private static readonly IReadOnlyDictionary<TuiThemeToken, Terminal.Gui.Attribute> TokenAttributes = new Dictionary<TuiThemeToken, Terminal.Gui.Attribute>
    {
        [TuiThemeToken.Text] = new(ColorName16.Gray, ColorName16.Black),
        [TuiThemeToken.Muted] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.Dim] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.Accent] = new(ColorName16.BrightCyan, ColorName16.Black),
        [TuiThemeToken.UserMessageBackground] = new(ColorName16.White, ColorName16.Blue),
        [TuiThemeToken.UserMessageText] = new(ColorName16.White, ColorName16.Blue),
        [TuiThemeToken.CustomMessageBackground] = new(ColorName16.White, ColorName16.Magenta),
        [TuiThemeToken.CustomMessageText] = new(ColorName16.White, ColorName16.Magenta),
        [TuiThemeToken.ToolPendingBackground] = new(ColorName16.White, ColorName16.Blue),
        [TuiThemeToken.ToolSuccessBackground] = new(ColorName16.White, ColorName16.Green),
        [TuiThemeToken.ToolErrorBackground] = new(ColorName16.White, ColorName16.Red),
        [TuiThemeToken.ToolOutput] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.MarkdownHeading] = new(ColorName16.Yellow, ColorName16.Black),
        [TuiThemeToken.MarkdownLink] = new(ColorName16.BrightBlue, ColorName16.Black),
        [TuiThemeToken.MarkdownLinkUrl] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.MarkdownCode] = new(ColorName16.BrightCyan, ColorName16.Black),
        [TuiThemeToken.MarkdownCodeBlock] = new(ColorName16.BrightGreen, ColorName16.Black),
        [TuiThemeToken.MarkdownQuote] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.MarkdownHorizontalRule] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.MarkdownListBullet] = new(ColorName16.BrightCyan, ColorName16.Black),
        [TuiThemeToken.BorderMuted] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.ThinkingText] = new(ColorName16.DarkGray, ColorName16.Black),
        [TuiThemeToken.Error] = new(ColorName16.White, ColorName16.Red),
        [TuiThemeToken.Success] = new(ColorName16.BrightGreen, ColorName16.Black),
        [TuiThemeToken.Warning] = new(ColorName16.Yellow, ColorName16.Black)
    };

    private static IReadOnlyDictionary<TuiThemeToken, string> _tokenHexPalette = TokenHexPalette;
    private static IReadOnlyDictionary<TuiThemeToken, Terminal.Gui.Attribute> _tokenAttributes = TokenAttributes;

    public static string ActiveThemeName { get; private set; } = DefaultThemeName;

    public static Terminal.Gui.Attribute UserRowAttribute => GetTokenAttribute(TuiThemeToken.UserMessageBackground);
    public static Terminal.Gui.Attribute ToolRunningRowAttribute => GetTokenAttribute(TuiThemeToken.ToolPendingBackground);
    public static Terminal.Gui.Attribute ToolSucceededRowAttribute => GetTokenAttribute(TuiThemeToken.ToolSuccessBackground);
    public static Terminal.Gui.Attribute ToolFailedRowAttribute => GetTokenAttribute(TuiThemeToken.ToolErrorBackground);
    public static Terminal.Gui.Attribute SystemRowAttribute => GetTokenAttribute(TuiThemeToken.Muted);
    public static Terminal.Gui.Attribute ErrorRowAttribute => GetTokenAttribute(TuiThemeToken.Error);
    public static Terminal.Gui.Attribute SelectionAttribute => new(ColorName16.Black, ColorName16.Gray);

    public static string GetTokenHex(TuiThemeToken token)
        => _tokenHexPalette.TryGetValue(token, out var hex) ? hex : Text;

    public static Terminal.Gui.Attribute GetTokenAttribute(TuiThemeToken token)
        => _tokenAttributes.TryGetValue(token, out var attribute) ? attribute : DefaultColorScheme.Normal;

    public static Terminal.Gui.Attribute ChatRowAttribute(TuiChatRowKind kind)
        => kind switch
        {
            TuiChatRowKind.Assistant => GetTokenAttribute(TuiThemeToken.Text),
            TuiChatRowKind.AssistantThinking => GetTokenAttribute(TuiThemeToken.ThinkingText),
            TuiChatRowKind.Custom => GetTokenAttribute(TuiThemeToken.CustomMessageBackground),
            TuiChatRowKind.User => UserRowAttribute,
            TuiChatRowKind.ToolRunning => ToolRunningRowAttribute,
            TuiChatRowKind.ToolSucceeded => ToolSucceededRowAttribute,
            TuiChatRowKind.ToolFailed => ToolFailedRowAttribute,
            TuiChatRowKind.System => SystemRowAttribute,
            TuiChatRowKind.Error => ErrorRowAttribute,
            _ => DefaultColorScheme.Normal
        };

    public static Terminal.Gui.Attribute SpanAttribute(TuiSpanKind kind, Terminal.Gui.Attribute fallback)
        => PreserveFallbackBackground(kind switch
        {
            TuiSpanKind.Muted => GetTokenAttribute(TuiThemeToken.Muted),
            TuiSpanKind.Accent => GetTokenAttribute(TuiThemeToken.Accent),
            TuiSpanKind.Success => GetTokenAttribute(TuiThemeToken.Success),
            TuiSpanKind.Warning => GetTokenAttribute(TuiThemeToken.Warning),
            TuiSpanKind.Error => GetTokenAttribute(TuiThemeToken.Error),
            TuiSpanKind.Code => GetTokenAttribute(TuiThemeToken.MarkdownCode),
            TuiSpanKind.Border => GetTokenAttribute(TuiThemeToken.BorderMuted),
            TuiSpanKind.Heading => GetTokenAttribute(TuiThemeToken.MarkdownHeading),
            TuiSpanKind.Link => GetTokenAttribute(TuiThemeToken.MarkdownLink),
            _ => fallback
        }, fallback);

    private static Terminal.Gui.Attribute PreserveFallbackBackground(Terminal.Gui.Attribute spanAttribute, Terminal.Gui.Attribute fallback)
    {
        if (spanAttribute.Background != ColorName16.Black || fallback.Background == ColorName16.Black)
        {
            return spanAttribute;
        }

        var foreground = spanAttribute.Foreground == ColorName16.DarkGray || spanAttribute.Foreground == ColorName16.Gray
            ? fallback.Foreground
            : spanAttribute.Foreground;
        return new Terminal.Gui.Attribute(foreground, fallback.Background);
    }

    public static void ApplyDefault()
    {
        ActiveThemeName = DefaultThemeName;
        _tokenHexPalette = TokenHexPalette;
        _tokenAttributes = TokenAttributes;
        ApplyRuntimeTheme(DefaultThemeName, DefaultColorScheme, PopupColorScheme, PopupColorScheme);
    }

    public static void Apply(TuiThemeDocument? document)
    {
        if (document is null)
        {
            ApplyDefault();
            return;
        }

        ActiveThemeName = string.IsNullOrWhiteSpace(document.Name) ? DefaultThemeName : document.Name;
        _tokenHexPalette = MergeTokenPalette(document.Tokens);
        _tokenAttributes = BuildAttributes(_tokenHexPalette);
        ApplyRuntimeTheme(
            ActiveThemeName,
            ApplyScheme(DefaultColorScheme, document.Default),
            ApplyScheme(PopupColorScheme, document.Dialog),
            ApplyScheme(PopupColorScheme, document.Menu));
    }

    private static IReadOnlyDictionary<TuiThemeToken, string> MergeTokenPalette(IReadOnlyDictionary<string, string>? tokens)
    {
        var merged = new Dictionary<TuiThemeToken, string>(TokenHexPalette);
        foreach (var pair in tokens ?? new Dictionary<string, string>())
            if (Enum.TryParse<TuiThemeToken>(pair.Key, ignoreCase: true, out var token) && !string.IsNullOrWhiteSpace(pair.Value))
                merged[token] = pair.Value;
        return merged;
    }

    private static IReadOnlyDictionary<TuiThemeToken, Terminal.Gui.Attribute> BuildAttributes(IReadOnlyDictionary<TuiThemeToken, string> palette)
    {
        var attributes = new Dictionary<TuiThemeToken, Terminal.Gui.Attribute>(TokenAttributes);
        foreach (var (token, hex) in palette)
            if (TryMapHexToColorName(hex, out var color))
                attributes[token] = new Terminal.Gui.Attribute(color, ColorName16.Black);
        return attributes;
    }

    private static ColorScheme ApplyScheme(ColorScheme fallback, TuiColorSchemeDocument? document)
        => document is null
            ? fallback
            : new ColorScheme
            {
                Normal = Build(document.NormalForeground, document.NormalBackground, fallback.Normal),
                Focus = Build(document.FocusForeground, document.FocusBackground, fallback.Focus),
                HotNormal = Build(document.HotNormalForeground, document.HotNormalBackground, fallback.HotNormal),
                HotFocus = Build(document.HotFocusForeground, document.HotFocusBackground, fallback.HotFocus),
                Disabled = Build(document.DisabledForeground, document.DisabledBackground, fallback.Disabled)
            };

    private static Terminal.Gui.Attribute Build(string? foreground, string? background, Terminal.Gui.Attribute fallback)
        => TryMapHexToColorName(foreground, out var fg) && TryMapHexToColorName(background, out var bg)
            ? new Terminal.Gui.Attribute(fg, bg)
            : fallback;

    private static bool TryMapHexToColorName(string? hex, out ColorName16 color)
    {
        color = ColorName16.Gray;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        color = hex.ToLowerInvariant() switch
        {
            "#000000" => ColorName16.Black,
            "#ffffff" => ColorName16.White,
            "#ff0000" or "#cc6666" => ColorName16.Red,
            "#00ff00" or "#b5bd68" => ColorName16.Green,
            "#0000ff" or "#5f87ff" => ColorName16.Blue,
            "#ffff00" => ColorName16.Yellow,
            "#00ffff" or "#8abeb7" or "#00d7ff" => ColorName16.Cyan,
            "#ff00ff" or "#b294bb" => ColorName16.Magenta,
            "#808080" or "#666666" or "#505050" => ColorName16.DarkGray,
            _ => ColorName16.Gray
        };
        return true;
    }

    private static void ApplyRuntimeTheme(string name, ColorScheme defaultScheme, ColorScheme dialogScheme, ColorScheme menuScheme)
    {
        ConfigurationManager.RuntimeConfig = $$"""
        {
          "Theme": "{{name}}"
        }
        """;
        ConfigurationManager.Apply();
        Colors.ColorSchemes[name] = defaultScheme;
        Colors.ColorSchemes["Dark"] = defaultScheme;
        Colors.ColorSchemes["Dialog"] = dialogScheme;
        Colors.ColorSchemes["Menu"] = menuScheme;
    }
}
