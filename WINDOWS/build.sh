#!/bin/bash
# Builds Poltergeist for Windows (x64) from macOS or Linux and zips it: dist/Poltergeist-win-x64.zip
# Uses the .NET SDK in .tools/dotnet if present, otherwise the one on PATH.
set -e
cd "$(dirname "$0")"
[ -x .tools/dotnet/dotnet ] && export PATH="$PWD/.tools/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

rm -rf dist/Poltergeist dist/Poltergeist-win-x64.zip
dotnet publish src/Poltergeist -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false -o dist/Poltergeist
cp README.md dist/Poltergeist/README.txt
(cd dist && zip -qr Poltergeist-win-x64.zip Poltergeist)
echo "Done: $(pwd)/dist/Poltergeist-win-x64.zip"
