using System.Text.Json;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

public sealed class ExtensionThemeBindingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetAllThemesAsync_ReturnsAllLoadedThemeDocuments()
    {
        await using var runtime = await CreateRuntimeAsync(
            [("a-dark.json", """{"name":"A Dark","tokens":{"accent":"#111"},"default":{"normalForeground":"#eee"}}"""),
             ("b-light.json", """{"name":"B Light","tokens":{"accent":"#fff"}}""")]);

        var themes = await runtime.ExtensionBinding.GetAllThemesAsync(CancellationToken.None);

        Assert.Equal(2, themes.Count);
        var dark = Assert.Single(themes, theme => theme.Name == "A Dark");
        Assert.NotNull(dark.Document);
        Assert.Equal("#111", dark.Document!.Tokens!["accent"]);
        Assert.Equal("#eee", dark.Document.Default!.NormalForeground);
        Assert.Contains(themes, theme => theme.Name == "B Light");
    }

    [Fact]
    public async Task GetThemeAsync_ReturnsSessionRuntimeTheme()
    {
        await using var runtime = await CreateRuntimeAsync(
            [("a-dark.json", """{"name":"A Dark"}"""),
             ("b-light.json", """{"name":"B Light"}""")]);

        var theme = await runtime.ExtensionBinding.GetThemeAsync(CancellationToken.None);

        Assert.NotNull(theme);
        Assert.Equal("A Dark", theme!.Name);
        Assert.NotNull(theme.Document);
    }

    [Fact]
    public async Task SetThemeAsync_UpdatesRuntimeThemeAndRaisesThemeChanged()
    {
        await using var runtime = await CreateRuntimeAsync(
            [("a-dark.json", """{"name":"A Dark"}"""),
             ("b-light.json", """{"name":"B Light"}""")]);
        var runtimeChanges = 0;
        runtime.ThemeChanged += (_, _) => runtimeChanges++;
        var bindingChanges = 0;
        runtime.ExtensionBinding.ThemeChanged += (_, _) => bindingChanges++;

        await runtime.ExtensionBinding.SetThemeAsync("B Light", CancellationToken.None);

        Assert.Equal("B Light", runtime.Theme?.Name);
        Assert.Equal("B Light", (await runtime.ExtensionBinding.GetThemeAsync(CancellationToken.None))?.Name);
        Assert.Equal(1, runtimeChanges);
        Assert.Equal(1, bindingChanges);
    }

    [Fact]
    public async Task SetThemeAsync_UnknownName_LeavesThemeUnchangedAndDoesNotRaise()
    {
        await using var runtime = await CreateRuntimeAsync([("a-dark.json", """{"name":"A Dark"}""")]);
        var changes = 0;
        runtime.ThemeChanged += (_, _) => changes++;

        await runtime.ExtensionBinding.SetThemeAsync("No Such Theme", CancellationToken.None);

        Assert.Equal("A Dark", runtime.Theme?.Name);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task SetThemeAsync_SameName_DoesNotRaiseThemeChanged()
    {
        await using var runtime = await CreateRuntimeAsync([("a-dark.json", """{"name":"A Dark"}""")]);
        var changes = 0;
        runtime.ThemeChanged += (_, _) => changes++;

        await runtime.ExtensionBinding.SetThemeAsync("a dark", CancellationToken.None);

        Assert.Equal("A Dark", runtime.Theme?.Name);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task WithoutThemePaths_GetAllThemesIsEmptyAndGetThemeIsNull()
    {
        await using var runtime = await CreateRuntimeAsync(themeFiles: null);

        Assert.Empty(await runtime.ExtensionBinding.GetAllThemesAsync(CancellationToken.None));
        Assert.Null(await runtime.ExtensionBinding.GetThemeAsync(CancellationToken.None));
    }

    [Fact]
    public void ExtensionThemeDocument_Json_DeserializesAsTuiThemeDocument()
    {
        var extensionTheme = new ExtensionThemeDocument(
            "compat",
            new Dictionary<string, string> { ["bg"] = "#000" },
            new ExtensionThemeColorScheme(NormalForeground: "#eee", NormalBackground: "#000"),
            null,
            new ExtensionThemeColorScheme(FocusForeground: "#fff"));

        var json = JsonSerializer.Serialize(extensionTheme, JsonOptions);
        var tuiTheme = JsonSerializer.Deserialize<TuiThemeDocument>(json, JsonOptions);

        Assert.NotNull(tuiTheme);
        Assert.Equal("compat", tuiTheme!.Name);
        Assert.Equal("#000", tuiTheme.Tokens!["bg"]);
        Assert.NotNull(tuiTheme.Default);
        Assert.Equal("#eee", tuiTheme.Default!.NormalForeground);
        Assert.Equal("#000", tuiTheme.Default!.NormalBackground);
        Assert.Null(tuiTheme.Dialog);
        Assert.NotNull(tuiTheme.Menu);
        Assert.Equal("#fff", tuiTheme.Menu!.FocusForeground);
    }

    [Fact]
    public void TuiThemeDocument_Json_DeserializesAsExtensionThemeDocument()
    {
        var tuiTheme = new TuiThemeDocument(
            "compat",
            new Dictionary<string, string> { ["bg"] = "#000" },
            new TuiColorSchemeDocument(NormalForeground: "#eee", NormalBackground: "#000"),
            null,
            new TuiColorSchemeDocument(FocusForeground: "#fff"));

        var json = JsonSerializer.Serialize(tuiTheme, JsonOptions);
        var extensionTheme = JsonSerializer.Deserialize<ExtensionThemeDocument>(json, JsonOptions);

        Assert.NotNull(extensionTheme);
        Assert.Equal("compat", extensionTheme!.Name);
        Assert.Equal("#000", extensionTheme.Tokens!["bg"]);
        Assert.NotNull(extensionTheme.Default);
        Assert.Equal("#eee", extensionTheme.Default!.NormalForeground);
        Assert.Equal("#000", extensionTheme.Default!.NormalBackground);
        Assert.Null(extensionTheme.Dialog);
        Assert.NotNull(extensionTheme.Menu);
        Assert.Equal("#fff", extensionTheme.Menu!.FocusForeground);
    }

    private static async Task<SessionRuntime> CreateRuntimeAsync(IReadOnlyList<(string FileName, string Json)>? themeFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-theme-binding-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);

        IReadOnlyList<string>? themePaths = null;
        if (themeFiles is not null)
        {
            var themesDir = Path.Combine(repo, "themes");
            Directory.CreateDirectory(themesDir);
            foreach (var (fileName, json) in themeFiles)
                await File.WriteAllTextAsync(Path.Combine(themesDir, fileName), json);
            themePaths = [themesDir];
        }

        return await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repo),
            HomeDirectory: Path.Combine(root, "home"),
            Resources: new RuntimeResourceOptions(
                ThemePaths: themePaths,
                DisableExtensions: true,
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableContextFiles: true)));
    }
}
