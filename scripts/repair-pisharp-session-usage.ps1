<#
.SYNOPSIS
Repairs historical PiSharp JSONL sessions so original JavaScript pi can resume them.

.DESCRIPTION
Older PiSharp session files can contain assistant messages with no usage object, or
with a usage object but no usage.cost object. The JavaScript pi footer assumes usage
and usage.cost.total exist and can crash when resuming those sessions.

This script recursively scans JSONL session files and adds missing zero-valued usage,
usage token fields, and usage.cost fields to assistant message entries. It is
idempotent and creates a backup beside every changed file by default.

.EXAMPLE
pwsh ./scripts/repair-pisharp-session-usage.ps1 -WhatIf

Preview the default session root without writing changes.

.EXAMPLE
pwsh ./scripts/repair-pisharp-session-usage.ps1

Repair all sessions under ~/.pi/agent/sessions, creating *.bak-* backups.

.EXAMPLE
pwsh ./scripts/repair-pisharp-session-usage.ps1 -SessionRoot C:\Users\me\.pi\agent\sessions -NoBackup

Repair a specific session root without creating backups.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SessionRoot = $(if ($HOME) { Join-Path $HOME ".pi/agent/sessions" } else { Join-Path $env:USERPROFILE ".pi/agent/sessions" }),
    [switch]$NoBackup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$usageFieldNames = @("input", "output", "cacheRead", "cacheWrite", "totalTokens")
$costFieldNames = @("input", "output", "cacheRead", "cacheWrite", "total")
$convertFromJsonParameters = @{ AsHashtable = $true }
if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey("DateKind")) {
    $convertFromJsonParameters["DateKind"] = "String"
}

function Test-Dictionary {
    param([object]$Value)
    return $Value -is [System.Collections.IDictionary]
}

function Ensure-NumberField {
    param(
        [System.Collections.IDictionary]$Object,
        [string]$Name
    )

    if (-not $Object.Contains($Name) -or $null -eq $Object[$Name]) {
        $Object[$Name] = 0
        return $true
    }

    return $false
}

function Repair-UsageObject {
    param([System.Collections.IDictionary]$Usage)

    $changed = $false
    foreach ($fieldName in $usageFieldNames) {
        $changed = (Ensure-NumberField -Object $Usage -Name $fieldName) -or $changed
    }

    if (-not $Usage.Contains("cost") -or -not (Test-Dictionary $Usage["cost"])) {
        $Usage["cost"] = [ordered]@{}
        $changed = $true
    }

    $cost = [System.Collections.IDictionary]$Usage["cost"]
    foreach ($fieldName in $costFieldNames) {
        $changed = (Ensure-NumberField -Object $cost -Name $fieldName) -or $changed
    }

    return $changed
}

function Repair-SessionLine {
    param(
        [string]$Line,
        [ref]$RepairedMessages,
        [ref]$SkippedLines
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $Line
    }

    try {
        $entry = ConvertFrom-Json -InputObject $Line @convertFromJsonParameters
    }
    catch {
        $SkippedLines.Value++
        return $Line
    }

    if (-not (Test-Dictionary $entry)) {
        return $Line
    }

    if ($entry["type"] -ne "message" -or -not (Test-Dictionary $entry["message"])) {
        return $Line
    }

    $message = [System.Collections.IDictionary]$entry["message"]
    if ($message["role"] -ne "assistant") {
        return $Line
    }

    $changed = $false
    if (-not $message.Contains("usage") -or -not (Test-Dictionary $message["usage"])) {
        $message["usage"] = [ordered]@{}
        $changed = $true
    }

    $changed = (Repair-UsageObject -Usage ([System.Collections.IDictionary]$message["usage"])) -or $changed
    if (-not $changed) {
        return $Line
    }

    $RepairedMessages.Value++
    return $entry | ConvertTo-Json -Compress -Depth 100
}

if (-not (Test-Path -LiteralPath $SessionRoot -PathType Container)) {
    throw "Session root does not exist: $SessionRoot"
}

$sessionFiles = @(Get-ChildItem -LiteralPath $SessionRoot -Filter "*.jsonl" -File -Recurse)
$filesChanged = 0
$messagesRepaired = 0
$skippedLines = 0
$backupStamp = Get-Date -Format "yyyyMMddHHmmss"

foreach ($file in $sessionFiles) {
    $raw = Get-Content -LiteralPath $file.FullName -Raw
    $hadTrailingNewline = $raw.EndsWith("`n")
    $lines = [regex]::Split($raw, "\r?\n")

    if ($hadTrailingNewline -and $lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq "") {
        $lines = $lines[0..($lines.Count - 2)]
    }

    $fileRepairedMessages = 0
    $newLines = foreach ($line in $lines) {
        Repair-SessionLine -Line $line -RepairedMessages ([ref]$fileRepairedMessages) -SkippedLines ([ref]$skippedLines)
    }

    if ($fileRepairedMessages -eq 0) {
        continue
    }

    $newRaw = $newLines -join "`n"
    if ($hadTrailingNewline) {
        $newRaw += "`n"
    }

    if ($PSCmdlet.ShouldProcess($file.FullName, "repair $fileRepairedMessages assistant usage object(s)")) {
        if (-not $NoBackup) {
            Copy-Item -LiteralPath $file.FullName -Destination "$($file.FullName).bak-$backupStamp" -Force
        }
        Set-Content -LiteralPath $file.FullName -Value $newRaw -NoNewline -Encoding UTF8
    }

    $filesChanged++
    $messagesRepaired += $fileRepairedMessages
}

[pscustomobject]@{
    SessionRoot = (Resolve-Path -LiteralPath $SessionRoot).Path
    FilesScanned = $sessionFiles.Count
    FilesChanged = $filesChanged
    MessagesRepaired = $messagesRepaired
    SkippedInvalidJsonLines = $skippedLines
    BackupsEnabled = -not $NoBackup
}
