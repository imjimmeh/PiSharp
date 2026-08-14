#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes every native extension project in the repository and installs the
    resulting plugin bundles into ~/.pi/extensions.

.DESCRIPTION
    Each plugin lands in its own folder: the entry assembly, its .deps.json, and
    the DLLs its publish output needs (NuGet packages such as
    Microsoft.CodeAnalysis or ModelContextProtocol, plus private support
    assemblies such as PiSharp.Plugins.ProtocolJsonRpc.dll or
    PiSharp.Memory.Abstractions.dll).

    App-base contract assemblies (PiSharp.Extensions.dll, PiSharp.Agent.Core.dll,
    ...) and other plugins' entry DLLs are never bundled: the host validates
    ExtensionMetadataAttribute and IExtension by type identity, so a plugin must
    resolve those from the host's own app base, not from a copy in its folder.
    Discovery skips support DLLs without ExtensionMetadataAttribute, so the
    dependency DLLs inside the folder are ignored and only the entry assembly is
    loaded.

    Companion plugins that register into a static registry living in another
    plugin's assembly (pisharp-mcp-transports-*, pisharp-eval-kernel-csharp)
    are excluded: every native plugin runs in its own load context, so their
    registrations never reach the owning plugin.

.EXAMPLE
    .\scripts\build-install-extensions.ps1
#>
param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$destRoot = Join-Path $env:USERPROFILE ".pi\extensions"
$cliOut = Join-Path $repo "src\PiSharp.Cli\bin\$Configuration\net10.0"

# Contract assemblies the host already ships in its app base. Bundling them next
# to a plugin makes the plugin's load context resolve its own copy, breaking the
# host's type-identity checks (ExtensionMetadataAttribute, IExtension) and any
# shared contract types. They must resolve from the app base instead.
$appBasePiSharp = @(Get-ChildItem $cliOut -Filter "PiSharp.*.dll" -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })

# Plugin projects whose output assembly carries [assembly: ExtensionMetadata].
$plugins = @(
    "PiSharp.Advisor",
    "PiSharp.AgentMessaging",
    "PiSharp.Ast",
    "PiSharp.Browser",
    "PiSharp.ContinualHarness",
    "PiSharp.Continuity",
    "PiSharp.Coordination",
    "PiSharp.DeclarativeTools",
    "PiSharp.Eval",
    "PiSharp.Extensions.Rules",
    "PiSharp.Git",
    "PiSharp.InternalUrls",
    "PiSharp.Mcp",
    "PiSharp.Memory",
    "PiSharp.Memory.Backends.File",
    "PiSharp.Memory.Backends.Off",
    "PiSharp.ModelRoles",
    "PiSharp.Permissions",
    "PiSharp.PlanMode",
    "PiSharp.Plugins.Debug",
    "PiSharp.Plugins.ForeignCompat",
    "PiSharp.Plugins.Lsp",
    "PiSharp.Research",
    "PiSharp.Research.Search.Brave",
    "PiSharp.Research.Search.GoogleCse",
    "PiSharp.Research.Search.Serper",
    "PiSharp.Subagents",
    "PiSharp.Telemetry.Otlp"
)

$entryDlls = @($plugins | ForEach-Object { "$_.dll" })

foreach ($p in $plugins) {
    if (-not $SkipBuild) {
        Write-Host "Publishing $p..."
        dotnet publish (Join-Path $repo "src\$p\$p.csproj") -c $Configuration --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $p" }
    }

    $outDir = Join-Path $repo "src\$p\bin\$Configuration\net10.0\publish"
    if (-not (Test-Path $outDir)) { throw "Publish output not found: $outDir" }
    $dest = Join-Path $destRoot $p
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Get-ChildItem $dest -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse
    Get-ChildItem $outDir -Include *.dll, *.deps.json -Recurse | ForEach-Object {
        # Skip other plugins' entry dlls and app-base contract assemblies.
        if ($entryDlls -contains $_.Name -and $_.Name -ne "$p.dll") { return }
        if ($appBasePiSharp -contains $_.Name -and $_.Name -ne "$p.dll") { return }
        Copy-Item $_.FullName (Join-Path $dest $_.Name) -Force
    }
    Write-Host "Installed $p -> $dest ($((Get-ChildItem $dest -Filter *.dll).Count) dlls)"
}

Write-Host "Done. $($plugins.Count) plugins installed under $destRoot"
