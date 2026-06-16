#!/usr/bin/env bash
set -euo pipefail

PACKAGE_ID="PiSharp.Cli"
COMMAND_NAME="pisharp"

if ! command -v dotnet &>/dev/null; then
    echo "Error: .NET SDK not found."
    echo "Install from: https://dot.net/download"
    exit 1
fi

if dotnet tool list --global 2>/dev/null | grep -qi "$PACKAGE_ID"; then
    echo "Updating $COMMAND_NAME..."
    dotnet tool update --global "$PACKAGE_ID"
else
    echo "Installing $COMMAND_NAME..."
    dotnet tool install --global "$PACKAGE_ID"
fi

echo ""
echo "Done! Run '$COMMAND_NAME' to launch the TUI, or '$COMMAND_NAME --help' for options."
echo "Make sure ~/.dotnet/tools is on your PATH."
