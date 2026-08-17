using PiSharp.Agent.Resources.Theme;
using PiSharp.Tui.Interactive.Rendering;
using Terminal.Gui;
using TGuiAttribute = Terminal.Gui.Attribute;

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

    private const string PageBgText = "#16130f";
    private const string TextHex = "#e8e0d5";
    private const string MutedHex = "#a89f8f";
    private const string DimHex = "#6b6255";
    private const string AccentHex = "#e8873a";
    private const string AccentBrightHex = "#f2a65a";
    private const string CoolContrastHex = "#7f9db9";
    private const string SuccessHex = "#a3c293";
    private const string ErrorHex = "#e07b67";
    private const string WarningHex = "#e5b45c";
    private const string KeywordHex = "#c9a0c8";
    private const string SelectedBgHex = "#3a3a4a";

    private const string UserMessageBgHex = "#1e1a12";
    private const string CustomMessageBgHex = "#1d1813";
    private const string ToolPendingBgHex = "#1c1a16";
    private const string ToolSuccessBgHex = "#1f2a1e";
    private const string ToolErrorBgHex = "#2a1a14";

    private static readonly Color PageBgColor = new("#16130f");
    private static readonly Color TextColor = new("#e8e0d5");
    private static readonly Color MutedColor = new("#a89f8f");

    private sealed record TokenPaletteEntry(string Foreground, string Background);

    private static readonly IReadOnlyDictionary<TuiThemeToken, TokenPaletteEntry> TokenPalette =
        new Dictionary<TuiThemeToken, TokenPaletteEntry>
        {
            [TuiThemeToken.Accent] = new(AccentHex, PageBgText),
            [TuiThemeToken.Border] = new(CoolContrastHex, PageBgText),
            [TuiThemeToken.BorderAccent] = new(AccentBrightHex, PageBgText),
            [TuiThemeToken.BorderMuted] = new(DimHex, PageBgText),
            [TuiThemeToken.Muted] = new(MutedHex, PageBgText),
            [TuiThemeToken.Dim] = new(DimHex, PageBgText),
            [TuiThemeToken.Text] = new(TextHex, PageBgText),
            [TuiThemeToken.Success] = new(SuccessHex, PageBgText),
            [TuiThemeToken.Error] = new(ErrorHex, PageBgText),
            [TuiThemeToken.Warning] = new(WarningHex, PageBgText),
            [TuiThemeToken.SelectedBackground] = new(TextHex, SelectedBgHex),
            [TuiThemeToken.PageBackground] = new(TextHex, PageBgText),
            [TuiThemeToken.UserMessageBackground] = new(TextHex, UserMessageBgHex),
            [TuiThemeToken.UserMessageText] = new(TextHex, UserMessageBgHex),
            [TuiThemeToken.CustomMessageBackground] = new(TextHex, CustomMessageBgHex),
            [TuiThemeToken.CustomMessageText] = new(TextHex, CustomMessageBgHex),
            [TuiThemeToken.ToolPendingBackground] = new(TextHex, ToolPendingBgHex),
            [TuiThemeToken.ToolSuccessBackground] = new(TextHex, ToolSuccessBgHex),
            [TuiThemeToken.ToolErrorBackground] = new(TextHex, ToolErrorBgHex),
            [TuiThemeToken.ToolOutput] = new(MutedHex, PageBgText),
            [TuiThemeToken.MarkdownHeading] = new(KeywordHex, PageBgText),
            [TuiThemeToken.MarkdownLink] = new(CoolContrastHex, PageBgText),
            [TuiThemeToken.MarkdownLinkUrl] = new(DimHex, PageBgText),
            [TuiThemeToken.MarkdownCode] = new(AccentHex, PageBgText),
            [TuiThemeToken.MarkdownCodeBlock] = new(SuccessHex, UserMessageBgHex),
            [TuiThemeToken.MarkdownQuote] = new(MutedHex, PageBgText),
            [TuiThemeToken.MarkdownHorizontalRule] = new(MutedHex, PageBgText),
            [TuiThemeToken.MarkdownListBullet] = new(AccentHex, PageBgText),
            [TuiThemeToken.ToolDiffAdded] = new(SuccessHex, PageBgText),
            [TuiThemeToken.ToolDiffRemoved] = new(ErrorHex, PageBgText),
            [TuiThemeToken.ToolDiffContext] = new(MutedHex, PageBgText),
            [TuiThemeToken.ThinkingText] = new(MutedHex, PageBgText),
            [TuiThemeToken.ThinkingOff] = new(DimHex, PageBgText),
            [TuiThemeToken.ThinkingMinimal] = new(DimHex, PageBgText),
            [TuiThemeToken.ThinkingLow] = new(CoolContrastHex, PageBgText),
            [TuiThemeToken.ThinkingMedium] = new(SuccessHex, PageBgText),
            [TuiThemeToken.ThinkingHigh] = new(AccentHex, PageBgText),
            [TuiThemeToken.ThinkingXHigh] = new(KeywordHex, PageBgText)
        };

    public static ColorScheme DefaultColorScheme { get; } = new()
    {
        Normal = new TGuiAttribute(new Color("#e8e0d5"), new Color("#16130f")),
        Focus = new TGuiAttribute(new Color("#e8873a"), new Color("#16130f")),
        HotNormal = new TGuiAttribute(new Color("#e8873a"), new Color("#16130f")),
        HotFocus = new TGuiAttribute(new Color("#f2a65a"), new Color("#3a3a4a")),
        Disabled = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f"))
    };

    public static ColorScheme PopupColorScheme { get; } = new()
    {
        Normal = new TGuiAttribute(new Color("#e8e0d5"), new Color("#3a3a4a")),
        Focus = new TGuiAttribute(new Color("#e8873a"), new Color("#3a3a4a")),
        HotNormal = new TGuiAttribute(new Color("#e8873a"), new Color("#3a3a4a")),
        HotFocus = new TGuiAttribute(new Color("#f2a65a"), new Color("#3a3a4a")),
        Disabled = new TGuiAttribute(new Color("#6b6255"), new Color("#3a3a4a"))
    };

    public static ColorScheme PromptBorderColorScheme { get; } = new()
    {
        Normal = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f")),
        Focus = new TGuiAttribute(new Color("#e8873a"), new Color("#16130f")),
        HotNormal = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f")),
        HotFocus = new TGuiAttribute(new Color("#e8873a"), new Color("#16130f")),
        Disabled = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f"))
    };

    public static ColorScheme AccentPromptBorderScheme { get; } = new()
    {
        Normal = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f")),
        Focus = new TGuiAttribute(new Color("#e8873a"), new Color("#16130f")),
        HotNormal = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f")),
        HotFocus = new TGuiAttribute(new Color("#f2a65a"), new Color("#16130f")),
        Disabled = new TGuiAttribute(new Color("#6b6255"), new Color("#16130f"))
    };

    private static IReadOnlyDictionary<TuiThemeToken, string> _tokenHexPalette =
        TokenPalette.ToDictionary(pair => pair.Key, pair => pair.Value.Foreground);

    private static IReadOnlyDictionary<TuiThemeToken, TGuiAttribute> _tokenAttributes = BuildAttributes();

    public static string ActiveThemeName { get; private set; } = DefaultThemeName;

    public static TGuiAttribute UserRowAttribute => GetTokenAttribute(TuiThemeToken.UserMessageBackground);
    public static TGuiAttribute ToolRunningRowAttribute => GetTokenAttribute(TuiThemeToken.ToolPendingBackground);
    public static TGuiAttribute ToolSucceededRowAttribute => GetTokenAttribute(TuiThemeToken.ToolSuccessBackground);
    public static TGuiAttribute ToolFailedRowAttribute => GetTokenAttribute(TuiThemeToken.ToolErrorBackground);
    public static TGuiAttribute SystemRowAttribute => GetTokenAttribute(TuiThemeToken.Muted);
    public static TGuiAttribute ErrorRowAttribute => GetTokenAttribute(TuiThemeToken.Error);
    public static TGuiAttribute SelectionAttribute => new(new Color("#f2a65a"), new Color("#3a3a4a"));

    public static string GetTokenHex(TuiThemeToken token)
    {
        if (!TokenPalette.TryGetValue(token, out var entry)) return TextHex;
        // Background band tokens report their band fill; foreground tokens report their color.
        return entry.Background != PageBgText
            ? entry.Background
            : _tokenHexPalette.TryGetValue(token, out var hex) ? hex : entry.Foreground;
    }

    public static TGuiAttribute GetTokenAttribute(TuiThemeToken token)
        => _tokenAttributes.TryGetValue(token, out var attribute) ? attribute : DefaultColorScheme.Normal;

    public static TGuiAttribute ChatRowAttribute(TuiChatRowKind kind)
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

    public static TGuiAttribute SpanAttribute(TuiSpanKind kind, TGuiAttribute fallback)
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

    private static TGuiAttribute PreserveFallbackBackground(TGuiAttribute spanAttribute, TGuiAttribute fallback)
    {
        // Muted/border spans are authored on the page background. When the target row also sits
        // on the page background, keep the span's own colors. When the row uses a distinct band
        // fill, adopt the row's text foreground so the span stays readable on the band.
        if (spanAttribute.Background != PageBgColor || fallback.Background == PageBgColor)
        {
            return spanAttribute;
        }

        var foreground = spanAttribute.Foreground == MutedColor
            || spanAttribute.Foreground == TextColor
            || spanAttribute.Foreground == new Color("#6b6255")
            ? fallback.Foreground
            : spanAttribute.Foreground;
        return new TGuiAttribute(foreground, fallback.Background);
    }

    public static void ApplyDefault()
    {
        ActiveThemeName = DefaultThemeName;
        _tokenHexPalette = TokenPalette.ToDictionary(pair => pair.Key, pair => pair.Value.Foreground);
        _tokenAttributes = BuildAttributes();
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
        _tokenAttributes = BuildAttributes();
        ApplyRuntimeTheme(
            ActiveThemeName,
            ApplyScheme(DefaultColorScheme, document.Default),
            ApplyScheme(PopupColorScheme, document.Dialog),
            ApplyScheme(PopupColorScheme, document.Menu));
    }

    private static IReadOnlyDictionary<TuiThemeToken, string> MergeTokenPalette(IReadOnlyDictionary<string, string>? tokens)
    {
        var merged = new Dictionary<TuiThemeToken, string>(
            TokenPalette.ToDictionary(pair => pair.Key, pair => pair.Value.Foreground));
        foreach (var pair in tokens ?? new Dictionary<string, string>())
            if (Enum.TryParse<TuiThemeToken>(pair.Key, ignoreCase: true, out var token) && !string.IsNullOrWhiteSpace(pair.Value))
                merged[token] = pair.Value;
        return merged;
    }

    private static IReadOnlyDictionary<TuiThemeToken, TGuiAttribute> BuildAttributes()
    {
        var attributes = new Dictionary<TuiThemeToken, TGuiAttribute>(TokenPalette.Count);
        foreach (var (token, entry) in TokenPalette)
        {
            // Theme overrides keep the token's background; foreground comes from the active
            // hex palette (which may have been re-mapped by Apply).
            var fgHex = _tokenHexPalette.TryGetValue(token, out var fg) ? fg : entry.Foreground;
            if (!Color.TryParse(fgHex, out var fgColor) || !Color.TryParse(entry.Background, out var bgColor)) continue;
            attributes[token] = new TGuiAttribute(fgColor!.Value, bgColor!.Value);
        }
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

    private static TGuiAttribute Build(string? foreground, string? background, TGuiAttribute fallback)
        => Color.TryParse(foreground, out var fg) && Color.TryParse(background, out var bg)
            ? new TGuiAttribute(fg!.Value, bg!.Value)
            : fallback;

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