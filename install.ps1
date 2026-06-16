#Requires -Version 5.1
param()

$PackageId   = "PiSharp.Cli"
$CommandName = "pisharp"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "Error: .NET SDK not found. Install from: https://dot.net/download"
    exit 1
}

$existing = dotnet tool list --global 2>$null | Select-String $CommandName
if ($existing) {
    Write-Host "Updating $CommandName..."
    dotnet tool update --global $PackageId
}
else {
    Write-Host "Installing $CommandName..."
    dotnet tool install --global $PackageId
}

Write-Host ""
Write-Host "Done! Run '$CommandName' to launch the TUI, or '$CommandName --help' for options."
Write-Host "Make sure %USERPROFILE%\.dotnet\tools is on your PATH."
