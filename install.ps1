#Requires -Version 5.1
param()

$PackageId   = "PiSharp.Cli"
$CommandName = "pisharp"
$CliProject  = Join-Path $PSScriptRoot "src\PiSharp.Cli\PiSharp.Cli.csproj"
$PackageDir  = Join-Path $PSScriptRoot "src\PiSharp.Cli\nupkg"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "Error: .NET SDK not found. Install from: https://dot.net/download"
    exit 1
}

Write-Host "Packing $PackageId from source..."
$version = Get-Date -Format "yyyy.MM.dd.HHmmss"
dotnet pack $CliProject -c Release --version $version -o $PackageDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to pack $PackageId. dotnet pack exited with code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$existing = dotnet tool list --global 2>$null | Select-String $CommandName
if ($existing) {
    Write-Host "Updating $CommandName."
    dotnet tool update --global --add-source $PackageDir $PackageId
}
else {
    Write-Host "Installing $CommandName."
    dotnet tool install --global --add-source $PackageDir $PackageId
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install $CommandName. dotnet tool exited with code $LASTEXITCODE."
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Done! Run '$CommandName' to launch the TUI, or '$CommandName --help' for options."
Write-Host "Make sure %USERPROFILE%\.dotnet\tools is on your PATH."
