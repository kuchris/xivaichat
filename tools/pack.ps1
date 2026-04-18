param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $repoRoot "XivAiChat\\bin\\$Configuration"
$distDir = Join-Path $repoRoot "dist"
$zipPath = Join-Path $distDir "XivAiChat.zip"
$manifestPath = Join-Path $buildDir "XivAiChat.json"
$repoJsonPath = Join-Path $repoRoot "repo.json"

if (-not (Test-Path $buildDir)) {
    throw "Build output not found: $buildDir"
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$files = @(
    (Join-Path $buildDir "XivAiChat.dll"),
    $manifestPath,
    (Join-Path $buildDir "XivAiChat.deps.json")
)

foreach ($file in $files) {
    if (-not (Test-Path $file)) {
        throw "Missing package file: $file"
    }
}

if (-not (Test-Path $repoJsonPath)) {
    throw "Missing repo manifest: $repoJsonPath"
}

$pluginManifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$repoEntries = @(Get-Content -Path $repoJsonPath -Raw | ConvertFrom-Json)

if ($repoEntries.Length -lt 1) {
    throw "repo.json does not contain any plugin entries."
}

$repoEntries[0].AssemblyVersion = $pluginManifest.AssemblyVersion
$repoEntries[0].TestingAssemblyVersion = $pluginManifest.AssemblyVersion
$repoEntries[0].DalamudApiLevel = $pluginManifest.DalamudApiLevel
$repoEntries[0].TestingDalamudApiLevel = $pluginManifest.DalamudApiLevel
$repoEntries[0].LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$repoJsonContent = ConvertTo-Json -InputObject @($repoEntries) -Depth 10
[System.IO.File]::WriteAllText(
    $repoJsonPath,
    $repoJsonContent,
    [System.Text.UTF8Encoding]::new($false))

Compress-Archive -Path $files -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
Write-Host "Updated $repoJsonPath to version $($pluginManifest.AssemblyVersion)"
