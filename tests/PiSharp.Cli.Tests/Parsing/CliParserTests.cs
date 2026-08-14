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
}
