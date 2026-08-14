using System.Text.Json;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using Xunit;

namespace PiSharp.ModelRoles.Tests;

public class ModelRolesSettingsParserTests
{
    private static JsonElement Section(string json) => JsonDocument.Parse(json).RootElement;

    private static string[] Selectors(ModelRoleResolution resolution) => [.. resolution.Selectors];

    // --- role value shapes ---

    [Fact]
    public void Parse_string_selector_creates_single_candidate_role()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@fast_worker": "anthropic/claude-haiku-4-5" }"""),
            effort: null);

        var resolution = Assert.Single(roles);
        Assert.Equal("fast_worker", resolution.Key);
        Assert.Equal(new[] { "anthropic/claude-haiku-4-5" }, Selectors(resolution.Value));
        Assert.Null(resolution.Value.Effort);
    }

    [Fact]
    public void Parse_array_keeps_prioritized_order_and_drops_duplicates()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@smol": ["openai-codex/gpt-5.4-mini", "anthropic/claude-haiku-4-5", "openai-codex/gpt-5.4-mini"] }"""),
            effort: null);

        var resolution = Assert.Single(roles).Value;
        Assert.Equal(
            new[] { "openai-codex/gpt-5.4-mini", "anthropic/claude-haiku-4-5" },
            Selectors(resolution));
    }

    [Fact]
    public void Parse_object_with_models_and_effort_applies_preset()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@review": { "models": "anthropic/claude-sonnet-4-5:high", "effort": "review" } }"""),
            Section("""{ "review": { "thinkingLevel": "high", "budgets": { "high": 24000, "xhigh": 48000 } } }"""));

        var resolution = Assert.Single(roles).Value;
        Assert.Equal(new[] { "anthropic/claude-sonnet-4-5:high" }, Selectors(resolution));
        Assert.NotNull(resolution.Effort);
        Assert.Equal(ThinkingLevel.High, resolution.Effort!.ThinkingLevel);
        Assert.Equal(24000, resolution.Effort!.Budgets!["high"]);
        Assert.Equal(48000, resolution.Effort!.Budgets["xhigh"]);
    }

    [Fact]
    public void Parse_object_with_models_array_and_effort()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@advisor": { "models": ["anthropic/claude-sonnet-4-5:medium", "anthropic/claude-opus-4-5:low"], "effort": "medium" } }"""),
            Section("""{ "medium": { "thinkingLevel": "medium" } }"""));

        var resolution = Assert.Single(roles).Value;
        Assert.Equal(
            new[] { "anthropic/claude-sonnet-4-5:medium", "anthropic/claude-opus-4-5:low" },
            Selectors(resolution));
        Assert.Equal(ThinkingLevel.Medium, resolution.Effort!.ThinkingLevel);
    }

    [Fact]
    public void Parse_normalizes_role_names_leading_at_and_case()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@Fast_Worker": "anthropic/claude-haiku-4-5", "SMOL": "openai-codex/gpt-5.4-mini" }"""),
            effort: null);

        Assert.Equal(2, roles.Count);
        Assert.True(roles.ContainsKey("fast_worker"));
        Assert.True(roles.ContainsKey("smol"));
    }

    [Fact]
    public void Parse_accepts_nested_role_selectors()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@fast_worker": "anthropic/claude-haiku-4-5", "@alias": ["@fast_worker", "anthropic/claude-opus-4-5"] }"""),
            effort: null);

        Assert.Equal(new[] { "@fast_worker", "anthropic/claude-opus-4-5" }, Selectors(roles["alias"]));
    }

    // --- diagnostics / invalid entries ---

    [Fact]
    public void Parse_unknown_effort_preset_reports_diagnostic_and_keeps_role_without_effort()
    {
        var diagnostics = new List<string>();
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@review": { "models": "anthropic/claude-sonnet-4-5", "effort": "missing" } }"""),
            Section("""{ "review": { "thinkingLevel": "high" } }"""),
            diagnostics.Add);

        Assert.Contains("review", roles.Keys);
        Assert.Null(roles["review"].Effort);
        Assert.Contains(diagnostics, d => d.Contains("unknown effort preset 'missing'", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_invalid_value_shape_skips_role_with_diagnostic()
    {
        var diagnostics = new List<string>();
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@ok": "anthropic/claude-haiku-4-5", "@bad": 42 }"""),
            effort: null,
            diagnostics.Add);

        Assert.Single(roles);
        Assert.Contains(diagnostics, d => d.Contains("Skipping model role '@bad'", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_object_without_models_skips_role_with_diagnostic()
    {
        var diagnostics = new List<string>();
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@broken": { "effort": "review" } }"""),
            effort: null,
            diagnostics.Add);

        Assert.Empty(roles);
        Assert.Contains(diagnostics, d => d.Contains("requires a 'models' property", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_invalid_selectors_and_keeps_valid_ones()
    {
        var diagnostics = new List<string>();
        var roles = ModelRolesSettingsParser.Parse(
            Section("""
                { "@mixed": [
                    "bare-id",
                    "/missing-provider",
                    "provider/",
                    "provider/id:bogus",
                    "@",
                    "anthropic/claude-haiku-4-5:high",
                    "@nested"
                ] }
                """),
            effort: null,
            diagnostics.Add);

        var resolution = Assert.Single(roles).Value;
        Assert.Equal(
            new[] { "anthropic/claude-haiku-4-5:high", "@nested" },
            Selectors(resolution));
        Assert.Equal(5, diagnostics.Count(d => d.Contains("Ignoring invalid model selector", StringComparison.Ordinal)));
    }

    [Fact]
    public void Parse_role_with_only_invalid_selectors_is_skipped()
    {
        var diagnostics = new List<string>();
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@lonely": ["no-slash", "also-no-slash"] }"""),
            effort: null,
            diagnostics.Add);

        Assert.Empty(roles);
        Assert.Contains(diagnostics, d => d.Contains("no valid selectors", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_effort_string_shorthand_sets_thinking_level()
    {
        var withEffort = ModelRolesSettingsParser.Parse(
            Section("""{ "@x": { "models": "anthropic/claude-haiku-4-5", "effort": "fast" } }"""),
            Section("""{ "fast": "low" }"""));

        Assert.Equal(ThinkingLevel.Low, withEffort["x"].Effort!.ThinkingLevel);
    }

    [Fact]
    public void Parse_absent_or_non_object_sections_yield_empty_map()
    {
        Assert.Empty(ModelRolesSettingsParser.Parse(null, null));
        Assert.Empty(ModelRolesSettingsParser.Parse(null, Section("""{ "review": "high" }""")));
        Assert.Empty(ModelRolesSettingsParser.Parse(Section("\"just-a-string\""), null));
    }

    // --- effort presets ---


    [Fact]
    public void Parse_effort_empty_object_is_legal_no_change_preset()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@x": { "models": "anthropic/claude-haiku-4-5", "effort": "empty" } }"""),
            Section("""{ "empty": { } }"""));

        var effort = roles["x"].Effort;
        Assert.NotNull(effort);
        Assert.Null(effort!.ThinkingLevel);
        Assert.Null(effort.Budgets);
    }

    [Fact]
    public void Parse_effort_normalizes_preset_names()
    {
        var roles = ModelRolesSettingsParser.Parse(
            Section("""{ "@x": { "models": "anthropic/claude-haiku-4-5", "effort": "Review" } }"""),
            Section("""{ "@review": "high" }"""));

        Assert.Equal(ThinkingLevel.High, roles["x"].Effort!.ThinkingLevel);
    }

    [Fact]
    public void Parse_effort_invalid_entries_are_skipped_with_diagnostic()
    {
        var diagnostics = new List<string>();
        ModelRolesSettingsParser.Parse(
            Section("""{ "@x": "anthropic/claude-haiku-4-5" }"""),
            Section("""
                {
                    "badLevel": "turbo",
                    "badKind": 42,
                    "ok": "high"
                }
                """),
            diagnostics.Add);

        Assert.Contains(diagnostics, d => d.Contains("not a valid thinking level", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("Skipping effort preset 'badKind'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("Skipping effort preset 'badLevel'", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_effort_invalid_budgets_are_skipped_with_diagnostic()
    {
        var diagnostics = new List<string>();
        var withEffort = ModelRolesSettingsParser.Parse(
            Section("""{ "@x": { "models": "anthropic/claude-haiku-4-5", "effort": "review" } }"""),
            Section("""{ "review": { "thinkingLevel": "high", "budgets": { "high": 24000, "xhigh": -1, "low": "many", "": 5 } } }"""),
            diagnostics.Add);

        var budgets = withEffort["x"].Effort!.Budgets!;
        Assert.Equal(24000, budgets["high"]);
        Assert.False(budgets.ContainsKey("xhigh"));
        Assert.False(budgets.ContainsKey("low"));
        Assert.Equal(2, diagnostics.Count(d => d.Contains("Ignoring invalid effort budget", StringComparison.Ordinal)));
        Assert.Contains(diagnostics, d => d.Contains("empty thinking-level name", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_invalid_thinking_level_in_object_is_ignored_with_diagnostic()
    {
        var diagnostics = new List<string>();
        var withEffort = ModelRolesSettingsParser.Parse(
            Section("""{ "@x": { "models": "anthropic/claude-haiku-4-5", "effort": "review" } }"""),
            Section("""{ "review": { "thinkingLevel": 42 } }"""),
            diagnostics.Add);

        Assert.Null(withEffort["x"].Effort!.ThinkingLevel);
        Assert.Contains(diagnostics, d => d.Contains("Ignoring invalid 'thinkingLevel'", StringComparison.Ordinal));
    }
}
