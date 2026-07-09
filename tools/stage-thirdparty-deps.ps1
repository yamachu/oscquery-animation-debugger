param(
    [string]$ProjectRoot = (Get-Location).Path,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRootResolved = (Resolve-Path -Path $ProjectRoot).Path
$packageRoot = Join-Path $projectRootResolved 'Assets/dev.yamachu.oscquery-animation-debugger'
$targetRoot = Join-Path $packageRoot 'Runtime/Plugins/ThirdParty'

$deps = @(
    @{
        Name = 'OscCore'
        Source = 'Assets/Packages/OscCore.1.0.5/lib/netstandard1.3/OscCore.dll'
        Dest = 'OscCore/OscCore.dll'
    },
    @{
        Name = 'VRChat.OSCQuery'
        Source = 'Assets/Packages/VRChat.OSCQuery.0.0.7/lib/net462/vrc-oscquery-lib.dll'
        Dest = 'VRChat.OSCQuery/vrc-oscquery-lib.dll'
    },
    @{
        Name = 'MeaMod.DNS'
        Source = 'Assets/Packages/MeaMod.DNS.1.0.70/lib/netstandard2.1/MeaMod.DNS.dll'
        Dest = 'MeaMod.DNS/MeaMod.DNS.dll'
    },
    @{
        Name = 'Microsoft.Extensions.Logging.Abstractions'
        Source = 'Assets/Packages/Microsoft.Extensions.Logging.Abstractions.6.0.2/lib/netstandard2.0/Microsoft.Extensions.Logging.Abstractions.dll'
        Dest = 'Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.Abstractions.dll'
    }
)

if ($Clean -and (Test-Path -LiteralPath $targetRoot)) {
    Write-Host "Cleaning existing staged dependencies: $targetRoot"
    Remove-Item -LiteralPath $targetRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

foreach ($dep in $deps) {
    $sourceDll = Join-Path $projectRootResolved $dep.Source
    $sourceMeta = "$sourceDll.meta"

    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "Missing source DLL for $($dep.Name): $sourceDll"
    }
    if (-not (Test-Path -LiteralPath $sourceMeta)) {
        throw "Missing source DLL meta for $($dep.Name): $sourceMeta"
    }

    $destDll = Join-Path $targetRoot $dep.Dest
    $destMeta = "$destDll.meta"
    $destDir = Split-Path -Path $destDll -Parent

    New-Item -ItemType Directory -Path $destDir -Force | Out-Null

    Copy-Item -LiteralPath $sourceDll -Destination $destDll -Force
    Copy-Item -LiteralPath $sourceMeta -Destination $destMeta -Force

    Write-Host "Staged $($dep.Name): $destDll"
}

Write-Host 'Done. Third-party dependencies staged for packaging.'
