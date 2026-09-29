$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\PixelPin.App\PixelPin.App.csproj'
$iconScript = Join-Path $root 'scripts\Generate-Icon.ps1'
$readmePath = Join-Path $root 'README.md'
$artifactRoot = Join-Path $root 'artifacts'
$publishDirectory = Join-Path $artifactRoot 'PixelPin-win-x64-portable'
$version = (dotnet msbuild $project -getProperty:Version -nologo | Select-Object -Last 1).Trim()
$zipPath = Join-Path $artifactRoot "PixelPin-$version-win-x64-portable.zip"
$hashPath = Join-Path $artifactRoot "SHA256SUMS-$version.txt"

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

& $iconScript

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDirectory

Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File | Remove-Item -Force

Copy-Item -LiteralPath $readmePath -Destination (Join-Path $publishDirectory 'README.md')

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath $hashPath -Value "$hash  $(Split-Path $zipPath -Leaf)" -Encoding ascii

Write-Host "Portable directory: $publishDirectory"
Write-Host "Archive: $zipPath"
Write-Host "SHA-256: $hash"
