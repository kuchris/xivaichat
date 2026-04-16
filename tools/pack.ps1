param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $repoRoot "XivAiChat\\bin\\$Configuration"
$distDir = Join-Path $repoRoot "dist"
$zipPath = Join-Path $distDir "XivAiChat.zip"

if (-not (Test-Path $buildDir)) {
    throw "Build output not found: $buildDir"
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$files = @(
    (Join-Path $buildDir "XivAiChat.dll"),
    (Join-Path $buildDir "XivAiChat.json"),
    (Join-Path $buildDir "XivAiChat.deps.json")
)

foreach ($file in $files) {
    if (-not (Test-Path $file)) {
        throw "Missing package file: $file"
    }
}

Compress-Archive -Path $files -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
