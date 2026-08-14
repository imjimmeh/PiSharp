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
}
