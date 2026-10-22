# Builds Poltergeist on Windows and zips it: dist\Poltergeist-win-x64.zip   (needs the .NET 8 SDK)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Test-Path dist\Poltergeist) { Remove-Item dist\Poltergeist -Recurse -Force }
dotnet publish src\Poltergeist -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o dist\Poltergeist
Remove-Item dist\Poltergeist\*.xml -ErrorAction SilentlyContinue
Copy-Item README.md dist\Poltergeist\README.txt
Compress-Archive -Path dist\Poltergeist -DestinationPath dist\Poltergeist-win-x64.zip -Force
Write-Host "Done: $PWD\dist\Poltergeist-win-x64.zip"
