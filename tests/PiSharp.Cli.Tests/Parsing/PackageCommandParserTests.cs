using PiSharp.Cli.Parsing;
using Xunit;

namespace PiSharp.Cli.Tests.Parsing;

public sealed class PackageCommandParserTests
{
    [Fact]
    public void ParsesInstallCommandWithSource()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Install, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.Source);
        Assert.False(args.PackageCommand.Local);
    }

    [Fact]
    public void ParsesInstallCommandWithLocalFlag()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar", "--local"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Install, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.Source);
        Assert.True(args.PackageCommand.Local);
    }

    [Fact]
    public void ParsesInstallCommandWithOfflineFlag()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar", "--offline"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Install, args.PackageCommand.Kind);
        Assert.True(args.Offline);
    }

    [Fact]
    public void ParsesInstallCommandWithForceFlag()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar@1.2.3", "--force"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Install, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar@1.2.3", args.PackageCommand.Source);
        Assert.True(args.PackageCommand.Force);
    }

    [Fact]
    public void HelpMentionsNativeDllInstall()
    {
        Assert.Contains("install <source>", CliHelpRenderer.Text);
        Assert.Contains(".dll", CliHelpRenderer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesRemoveCommandWithSource()
    {
        var args = CliParser.Parse(["remove", "npm:@foo/bar"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Remove, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.Source);
    }

    [Fact]
    public void ParsesUninstallAliasAsRemove()
    {
        var args = CliParser.Parse(["uninstall", "npm:@foo/bar"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Remove, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.Source);
    }

    [Fact]
    public void ParsesUpdateCommandWithNoArgs()
    {
        var args = CliParser.Parse(["update"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.Null(args.PackageCommand.Source);
        Assert.False(args.PackageCommand.Self);
        Assert.False(args.PackageCommand.Extensions);
    }

    [Fact]
    public void ParsesUpdatePiCommand()
    {
        var args = CliParser.Parse(["update", "pi"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.True(args.PackageCommand.Self);
        Assert.False(args.PackageCommand.Extensions);
    }

    [Fact]
    public void ParsesUpdateSelfCommand()
    {
        var args = CliParser.Parse(["update", "self"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.True(args.PackageCommand.Self);
    }

    [Fact]
    public void ParsesUpdateSelfFlag()
    {
        var args = CliParser.Parse(["update", "--self"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.True(args.PackageCommand.Self);
    }

    [Fact]
    public void ParsesUpdateExtensionsFlag()
    {
        var args = CliParser.Parse(["update", "--extensions"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.True(args.PackageCommand.Extensions);
    }

    [Fact]
    public void ParsesUpdateExtensionWithSource()
    {
        var args = CliParser.Parse(["update", "--extension", "npm:@foo/bar"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.False(args.PackageCommand.Extensions);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.ExtensionSource);
    }

    [Fact]
    public void ParsesUpdateCommandWithOfflineFlag()
    {
        var args = CliParser.Parse(["update", "--offline"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.True(args.Offline);
    }

    [Fact]
    public void ParsesUpdateCommandWithForceFlag()
    {
        var args = CliParser.Parse(["update", "npm:@foo/bar@1.2.3", "--force"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar@1.2.3", args.PackageCommand.Source);
        Assert.True(args.PackageCommand.Force);
    }

    [Fact]
    public void ParsesUpdateWithSource()
    {
        var args = CliParser.Parse(["update", "npm:@foo/bar"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Update, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.Source);
    }

    [Fact]
    public void ParsesListCommand()
    {
        var args = CliParser.Parse(["list"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.List, args.PackageCommand.Kind);
    }

    [Fact]
    public void PackageCommandsAreNotTreatedAsPromptText()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar"]);

        Assert.Empty(args.MessagesOrEmpty);
    }

    [Fact]
    public void ReportsErrorForUnknownPackageOption()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar", "--unknown-opt"]);

        Assert.Contains(args.DiagnosticsOrEmpty, d =>
            d.Type == CliDiagnosticType.Error && d.Message.Contains("--unknown-opt"));
    }

    [Fact]
    public void ReportsErrorForMissingOptionValue()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar", "--local", "--extension"]);

        Assert.Contains(args.DiagnosticsOrEmpty, d =>
            d.Type == CliDiagnosticType.Error && d.Message.Contains("--extension"));
    }

    [Fact]
    public void ReportsErrorForConflictingUpdateTargets()
    {
        var args = CliParser.Parse(["update", "pi", "--extensions"]);

        Assert.Contains(args.DiagnosticsOrEmpty, d =>
            d.Type == CliDiagnosticType.Error && d.Message.Contains("pi") && d.Message.Contains("--extensions"));
    }

    [Fact]
    public void CommandsBeforeAppModeSelectionBehaveCorrectly()
    {
        var args = CliParser.Parse(["install", "npm:@foo/bar", "--mode", "json"]);

        Assert.NotNull(args.PackageCommand);
        Assert.Equal(PackageCommandKind.Install, args.PackageCommand.Kind);
        Assert.Equal("npm:@foo/bar", args.PackageCommand.Source);
        Assert.Null(args.Mode);
    }
}
