param(
    [string]$ProjectRoot = (Get-Location).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRootResolved = (Resolve-Path -Path $ProjectRoot).Path
$packageRoot = Join-Path $projectRootResolved 'Assets/dev.yamachu.oscquery-animation-debugger'
$packageJsonPath = Join-Path $packageRoot 'package.json'
$artifactsDir = Join-Path $projectRootResolved 'Artifacts'

if (-not (Test-Path -LiteralPath $packageJsonPath)) {
    throw "package.json not found: $packageJsonPath"
}

$packageJsonRaw = Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8
$packageJson = $packageJsonRaw | ConvertFrom-Json
$packageName = [string]$packageJson.name
$packageVersion = [string]$packageJson.version

if ([string]::IsNullOrWhiteSpace($packageName) -or [string]::IsNullOrWhiteSpace($packageVersion)) {
    throw 'package.json must include both name and version.'
}

New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null

$writeProbe = Join-Path $artifactsDir '.write-probe.tmp'
try {
    Set-Content -LiteralPath $writeProbe -Value 'ok'
    Remove-Item -LiteralPath $writeProbe -Force
}
catch {
    throw "Artifacts directory is not writable: $artifactsDir. If running in CI after unity-builder, fix ownership/permissions before packaging. Original: $($_.Exception.Message)"
}

$zipFileName = "$packageName-$packageVersion.zip"
$zipPath = Join-Path $artifactsDir $zipFileName
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$tempRoot = Join-Path $projectRootResolved '.tmp-vpm-package'
if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

$stagingDir = Join-Path $tempRoot $packageName
Copy-Item -LiteralPath $packageRoot -Destination $stagingDir -Recurse -Force

Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = Join-Path $artifactsDir "$zipFileName.sha256.txt"
Set-Content -LiteralPath $hashFile -Value $hash

Remove-Item -LiteralPath $tempRoot -Recurse -Force

Write-Host "Created VPM zip: $zipPath"
Write-Host "SHA256: $hash"
