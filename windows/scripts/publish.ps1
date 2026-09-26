# Builds ScreenPlus for distribution into windows\dist:
#   dist\win-<arch>\ScreenPlus.exe        a single self-contained exe (runs without installing .NET)
#   dist\ScreenPlus-Setup-<ver>-<arch>.exe  with -Installer: a setup program (needs Inno Setup 6.3+)
#
#   .\scripts\publish.ps1                       64-bit Intel/AMD PCs
#   .\scripts\publish.ps1 -Runtime win-arm64    Windows on Arm
#   .\scripts\publish.ps1 -Installer            also build the installer
#   .\scripts\publish.ps1 -Run                  build, then start it
param(
    [string]$Runtime = "win-x64",
    [switch]$Installer,
    [switch]$Run
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$arch = $Runtime -replace '^win-', ''

$out = "dist/$Runtime"
dotnet publish src/ScreenPlus -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $out/ScreenPlus.exe"

if ($Installer) {
    # The installer ships the app as a folder: it starts faster than the single exe, which unpacks itself.
    $app = "dist/app-$Runtime"
    dotnet publish src/ScreenPlus -c Release -r $Runtime --self-contained -o $app
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
    if (-not (Test-Path $iscc)) { throw "Inno Setup 6 isn't installed. Get it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup)." }
    & $iscc "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$(Resolve-Path $app)" installer/ScreenPlus.iss
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Built dist/ScreenPlus-Setup-$version-$arch.exe"
}

if ($Run) { Start-Process "$out/ScreenPlus.exe" }
