# Builds a self-contained ScreenPlus.exe (runs without installing .NET) into windows\dist\<runtime>.
#
#   .\scripts\publish.ps1                     64-bit Intel/AMD PCs
#   .\scripts\publish.ps1 -Runtime win-arm64  Windows on Arm
#   .\scripts\publish.ps1 -Run                build, then start it
param(
    [string]$Runtime = "win-x64",
    [switch]$Run
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$out = "dist/$Runtime"
dotnet publish src/ScreenPlus -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $out/ScreenPlus.exe"

if ($Run) { Start-Process "$out/ScreenPlus.exe" }
