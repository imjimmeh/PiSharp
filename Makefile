.PHONY: build install build-and-install

user_home := C:/Users/jimme
app_location := $(user_home)/.local/share/PiSharp
bin_location := $(user_home)/.local/bin/PiSharp.bat
cli_project := ./src/PiSharp.Cli
build_location := $(cli_project)/bin/Release/net10.0
package_location := $(cli_project)/nupkg

build-and-install: build install

build:
	@echo "Building the project..."
	dotnet build --configuration Release

install:
	dotnet pack $(cli_project)/PiSharp.Cli.csproj -c Release --version $(shell date +%Y.%m.%d.%H%M%S) -o $(package_location)
	dotnet tool install --global --add-source $(package_location) PiSharp.Cli