#!/usr/bin/env bash
set -euo pipefail

PACKAGE_ID="PiSharp.Cli"
COMMAND_NAME="pisharp"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLI_PROJECT="$SCRIPT_DIR/src/PiSharp.Cli/PiSharp.Cli.csproj"
PACKAGE_DIR="$SCRIPT_DIR/src/PiSharp.Cli/nupkg"

if ! command -v dotnet &>/dev/null; then
    echo "Error: .NET SDK not found."
    echo "Install from: https://dot.net/download"
    exit 1
fi

echo "Packing $PACKAGE_ID from source..."
VERSION="$(date +%Y.%m.%d.%H%M%S)"
dotnet pack "$CLI_PROJECT" -c Release --version "$VERSION" -o "$PACKAGE_DIR"

if dotnet tool list --global 2>/dev/null | grep -qi "$PACKAGE_ID"; then
    echo "Updating $COMMAND_NAME..."
    dotnet tool update --global --add-source "$PACKAGE_DIR" "$PACKAGE_ID"
else
    echo "Installing $COMMAND_NAME..."
    dotnet tool install --global --add-source "$PACKAGE_DIR" "$PACKAGE_ID"
fi

echo ""
echo "Done! Run '$COMMAND_NAME' to launch the TUI, or '$COMMAND_NAME --help' for options."
echo "Make sure ~/.dotnet/tools is on your PATH."
