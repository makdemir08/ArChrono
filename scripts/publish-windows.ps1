# ArChrono'yu Windows için tek dosyalık, self-contained exe olarak yayınlar.
# Kullanım: .\scripts\publish-windows.ps1 [-Runtime win-x64|win-arm64] [-Zip]
param(
    [string]$Runtime = "win-x64",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/windows/$Runtime"
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version

dotnet publish "$root/src/ArChrono.App/ArChrono.App.csproj" -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $out

if ($Zip) {
    $dist = Join-Path $root "dist"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $arch = if ($Runtime -eq "win-arm64") { "ARM64" } else { "x64" }
    $zipPath = Join-Path $dist "ArChrono-$version-Windows-$arch-Portable.zip"
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zipPath -Force
    Write-Host "Hazır: $zipPath"
}

Write-Host "Hazır: $out\ArChrono.exe"
Write-Host "Not: ArChrono sistemdeki Git'i kullanır (Git for Windows 2.38+)."
