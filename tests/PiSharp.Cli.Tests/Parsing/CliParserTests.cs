using PiSharp.Cli.Parsing;
using Xunit;

namespace PiSharp.Cli.Tests.Parsing;

public sealed class CliParserTests
{
    [Fact]
    public void KeepsModeModelProviderLongOnlyAndPrintShortAlias()
    {
        var args = CliParser.Parse(["--mode", "json", "--provider", "anthropic", "--model", "claude", "-p", "hello"]);

        Assert.Equal(CliMode.Json, args.Mode);
        Assert.Equal("anthropic", args.Provider);
        Assert.Equal("claude", args.Model);
        Assert.True(args.Print);
        Assert.Equal(["hello"], args.MessagesOrEmpty);
    }

    [Fact]
    public void PreservesUnknownLongAndRejectsUnknownShort()
    {
        var args = CliParser.Parse(["--future", "value", "-m"]);

        Assert.Equal("value", args.UnknownFlagsOrEmpty["future"]);
        Assert.Contains(args.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("-m"));
    }

    [Fact]
    public void PreservesAtFileArgsAndTripleDashPrintPrompt()
    {
        var args = CliParser.Parse(["@prompt.md", "-p", "--- explain"]);

        Assert.Equal(["prompt.md"], args.FileArgsOrEmpty);
        Assert.Equal(["--- explain"], args.MessagesOrEmpty);
    }

    [Fact]
    public void SelectsModeByExplicitModeThenPrintThenRedirectedStdin()
    {
        Assert.Equal(AppMode.Rpc, CliParser.SelectAppMode(new CliArgs(Mode: CliMode.Rpc, Print: true), stdinRedirected: true));
        Assert.Equal(AppMode.PrintJson, CliParser.SelectAppMode(new CliArgs(Mode: CliMode.Json), stdinRedirected: false));
        Assert.Equal(AppMode.PrintText, CliParser.SelectAppMode(new CliArgs(Mode: CliMode.Text), stdinRedirected: false));
        Assert.Equal(AppMode.PrintText, CliParser.SelectAppMode(new CliArgs(Print: true), stdinRedirected: false));
        Assert.Equal(AppMode.PrintText, CliParser.SelectAppMode(new CliArgs(), stdinRedirected: true));
        Assert.Equal(AppMode.Interactive, CliParser.SelectAppMode(new CliArgs(), stdinRedirected: false));
    }

    [Fact]
    public void ParsesSubagentJsonMode()
    {
        var args = CliParser.Parse(["--mode", "subagent-json"]);

        Assert.Equal(CliMode.SubagentJson, args.Mode);
        Assert.Equal(AppMode.SubagentJson, CliParser.SelectAppMode(args, stdinRedirected: false));
    }

    [Fact]
    public void RoutesJsPiSubagentJsonInvocationToCompatibleJsonMode()
    {
        var args = CliParser.Parse(["--mode", "json", "-p", "--no-session", "Task: inspect files"]);

        Assert.Equal(CliMode.Json, args.Mode);
        Assert.True(args.Print);
        Assert.True(args.NoSession);
        Assert.Equal(["Task: inspect files"], args.MessagesOrEmpty);
        Assert.Equal(AppMode.SubagentJson, CliParser.SelectAppMode(args, stdinRedirected: false));
    }

    [Fact]
    public void ParsesBenchmarkStartupFlag()
    {
        var args = CliParser.Parse(["--benchmark-startup"]);

        Assert.True(args.BenchmarkStartup);
    }

    [Fact]
    public void ParsesNoResourcesFlag()
    {
        var args = CliParser.Parse(["--no-resources"]);

        Assert.True(args.NoResources);
        Assert.False(args.NoTools);
    }

    [Fact]
    public void Config_command_is_parsed_as_package_command()
    {
        var result = CliParser.Parse(["config"]);

        Assert.NotNull(result.PackageCommand);
        Assert.Equal(PackageCommandKind.Config, result.PackageCommand.Kind);
    }

    [Fact]
    public void Config_command_does_not_consume_next_arg()
    {
        var result = CliParser.Parse(["config", "--verbose"]);

        Assert.Equal(PackageCommandKind.Config, result.PackageCommand!.Kind);
        Assert.True(result.Verbose);
    }

    [Fact]
    public void ParsesAcpMode()
    {
        var args = CliParser.Parse(["--mode", "acp"]);

        Assert.Equal(CliMode.Acp, args.Mode);
        Assert.Equal(AppMode.Acp, CliParser.SelectAppMode(args, stdinRedirected: false));
    }

    [Fact]
    public void ParsesApprovalModes()
    {
        Assert.Equal(AcpApprovalMode.Yolo, CliParser.Parse(["--approval-mode", "yolo"]).ApprovalMode);
        Assert.Equal(AcpApprovalMode.Ask, CliParser.Parse(["--approval-mode", "ask"]).ApprovalMode);
        Assert.Equal(AcpApprovalMode.ReadOnly, CliParser.Parse(["--approval-mode", "read-only"]).ApprovalMode);
    }

    [Fact]
    public void InvalidApprovalModeProducesDiagnostic()
    {
        var args = CliParser.Parse(["--approval-mode", "bogus"]);

        Assert.Null(args.ApprovalMode);
        Assert.Contains(args.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Warning && d.Message.Contains("bogus"));
    }

    [Fact]
    public void ParsesProfileFlag()
    {
        var args = CliParser.Parse(["--profile", "work"]);

        Assert.Equal("work", args.Profile);
    }

    [Fact]
    public void ParsesCheckUpdatesFlags()
    {
        Assert.True(CliParser.Parse(["--check-updates"]).CheckUpdates);
        Assert.True(CliParser.Parse(["--no-check-updates"]).NoCheckUpdates);
    }

    [Fact]
    public void ParsesAddSourceOnUpdate()
    {
        var args = CliParser.Parse(["update", "pi", "--add-source", "https://feed.local/v3/index.json"]);

        Assert.NotNull(args.PackageCommand);
        Assert.True(args.PackageCommand.Self);
        Assert.Equal("https://feed.local/v3/index.json", args.PackageCommand.AddSource);
    }

    [Fact]
    public void Parse_DaemonCommand_ProducesDaemonArgs()
    {
        var parsed = CliParser.Parse(["daemon", "start", "--port", "7878"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.Equal("7878", parsed.DaemonCommand.Port);
    }

    [Fact]
    public void Parse_DaemonStatusCommand_ProducesStatusArgs()
    {
        var parsed = CliParser.Parse(["daemon", "status"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Status, parsed.DaemonCommand.Kind);
    }

    [Fact]
    public void Parse_DaemonStartWithForegroundFlag_SetsForeground()
    {
        var parsed = CliParser.Parse(["daemon", "start", "--foreground"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.True(parsed.DaemonCommand.Foreground);
    }

    [Fact]
    public void Parse_DaemonForegroundKind_NormalizesToStartWithForeground()
    {
        var parsed = CliParser.Parse(["daemon", "foreground"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.True(parsed.DaemonCommand.Foreground);
        Assert.Null(parsed.DaemonCommand.Port);
    }

    [Fact]
    public void Parse_DaemonStop_ProducesStopArgs()
    {
        var parsed = CliParser.Parse(["daemon", "stop"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Stop, parsed.DaemonCommand.Kind);
    }

    [Fact]
    public void Parse_DaemonAlone_ProducesErrorAndNoDaemonCommand()
    {
        var parsed = CliParser.Parse(["daemon"]);

        Assert.Null(parsed.DaemonCommand);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("Missing subcommand"));
    }

    [Fact]
    public void Parse_DaemonUnknownKind_ProducesErrorAndNoDaemonCommand()
    {
        var parsed = CliParser.Parse(["daemon", "foo"]);

        Assert.Null(parsed.DaemonCommand);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("foo"));
    }

    [Fact]
    public void Parse_DaemonFlagBeforeKind_ProducesErrorAndNoDaemonCommand()
    {
        var parsed = CliParser.Parse(["daemon", "--port", "5"]);

        Assert.Null(parsed.DaemonCommand);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--port"));
    }

    [Fact]
    public void Parse_DaemonStartMissingPortValue_ProducesError()
    {
        var parsed = CliParser.Parse(["daemon", "start", "--port"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.Null(parsed.DaemonCommand.Port);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--port"));
    }

    [Fact]
    public void Parse_DaemonStartPortRejectsFlagLikeValue()
    {
        var parsed = CliParser.Parse(["daemon", "start", "--port", "--foreground"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.Null(parsed.DaemonCommand.Port);
        Assert.True(parsed.DaemonCommand.Foreground);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--port"));
    }

    [Fact]
    public void Parse_DaemonAfterOtherFlags_StillParsesAsDaemonCommand()
    {
        var parsed = CliParser.Parse(["--verbose", "daemon", "status"]);

        Assert.True(parsed.Verbose);
        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Status, parsed.DaemonCommand.Kind);
    }

    [Fact]
    public void Parse_DaemonStartApiKey_ProducesApiKey()
    {
        var parsed = CliParser.Parse(["daemon", "start", "--api-key", "abc123"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.Equal("abc123", parsed.DaemonCommand.ApiKey);
    }

    [Fact]
    public void Parse_DaemonStartApiKeyRejectsFlagLikeValue()
    {
        var parsed = CliParser.Parse(["daemon", "start", "--api-key", "--port", "7878"]);

        Assert.NotNull(parsed.DaemonCommand);
        Assert.Equal(DaemonCommandKind.Start, parsed.DaemonCommand.Kind);
        Assert.Null(parsed.DaemonCommand.ApiKey);
        Assert.Equal("7878", parsed.DaemonCommand.Port);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--api-key"));
    }

    [Fact]
    public void Parse_LocalFlag_SetsLocal()
    {
        var parsed = CliParser.Parse(["--local"]);

        Assert.True(parsed.Local);
    }

    [Fact]
    public void Parse_NoLocalFlag_DefaultsFalse()
    {
        var parsed = CliParser.Parse([]);

        Assert.False(parsed.Local);
    }

    [Fact]
    public void Parse_AttachFlag_ParsesSessionId()
    {
        var parsed = CliParser.Parse(["--attach", "sess-123"]);

        Assert.Equal("sess-123", parsed.Attach);
        Assert.Empty(parsed.DiagnosticsOrEmpty);
    }

    [Fact]
    public void Parse_AttachWithoutValue_ProducesDiagnostic()
    {
        var parsed = CliParser.Parse(["--attach"]);

        Assert.Null(parsed.Attach);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--attach"));
    }

    [Fact]
    public void Parse_AttachAndLocal_Coexist()
    {
        var parsed = CliParser.Parse(["--attach", "sess-123", "--local"]);

        Assert.Equal("sess-123", parsed.Attach);
        Assert.True(parsed.Local);
    }

    [Fact]
    public void Parse_AttachAndResume_Coexist()
    {
        var parsed = CliParser.Parse(["--attach", "sess-123", "--resume"]);

        Assert.Equal("sess-123", parsed.Attach);
        Assert.True(parsed.Resume);
        Assert.Empty(parsed.DiagnosticsOrEmpty);
    }

    [Fact]
    public void Parse_Stats_BareKeyword()
    {
        var parsed = CliParser.Parse(["stats"]);

        Assert.NotNull(parsed.StatsCommand);
        Assert.Null(parsed.StatsCommand.Since);
        Assert.False(parsed.StatsCommand.Json);
        Assert.False(parsed.StatsCommand.Live);
        Assert.Empty(parsed.DiagnosticsOrEmpty);
    }
    [Theory]
    [InlineData("stats --json", true, false)]
    [InlineData("stats --live", false, true)]
    [InlineData("stats --json --live", true, true)]
    public void Parse_Stats_Flags(string commandLine, bool json, bool live)
    {
        var parsed = CliParser.Parse(commandLine.Split(' '));

        Assert.NotNull(parsed.StatsCommand);
        Assert.Equal(json, parsed.StatsCommand.Json);
        Assert.Equal(live, parsed.StatsCommand.Live);
        Assert.Empty(parsed.DiagnosticsOrEmpty);
    }

    [Theory]
    [InlineData("stats --since 6h", "06:00:00")]
    [InlineData("stats --since 30d", "30.00:00:00")]
    [InlineData("stats --since 00:05:00", "00:05:00")]
    public void Parse_Stats_SinceDuration(string commandLine, string expected)
    {
        var parsed = CliParser.Parse(commandLine.Split(' '));

        Assert.NotNull(parsed.StatsCommand);
        Assert.Equal(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), parsed.StatsCommand.Since);
    }


    [Fact]
    public void Parse_Stats_UnknownFlagEmitsDiagnostic()
    {
        var parsed = CliParser.Parse(["stats", "--bogus"]);

        Assert.NotNull(parsed.StatsCommand);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--bogus"));
    }

    [Fact]
    public void Parse_Stats_InvalidSinceEmitsDiagnostic()
    {
        var parsed = CliParser.Parse(["stats", "--since", "nonsense"]);

        Assert.NotNull(parsed.StatsCommand);
        Assert.Contains(parsed.DiagnosticsOrEmpty, d => d.Type == CliDiagnosticType.Error && d.Message.Contains("--since"));
    }

    [Fact]
    public void Parse_Stats_DoesNotEatFollowingPositionalPrompt()
    {
        var parsed = CliParser.Parse(["stats", "hello world"]);

        Assert.NotNull(parsed.StatsCommand);
        Assert.Equal(["hello world"], parsed.MessagesOrEmpty);
    }
}
