$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src'

$sources = Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
$forbidden = $sources | Select-String -Pattern '\b(DllImport|LibraryImport|NativeLibrary\.Load|Process\.Start|ProcessStartInfo)\b'
if ($forbidden) { throw "Native interop or process invocation found: $($forbidden.Path -join ', ')" }

foreach ($project in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter *.csproj) {
    $assetsPath = Join-Path $project.DirectoryName 'obj/project.assets.json'
    if (!(Test-Path -LiteralPath $assetsPath)) { throw "Restore $($project.Name) before auditing dependencies." }
    $assets = Get-Content -LiteralPath $assetsPath -Encoding UTF8 -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Name -match '^(Qcxt|FFmpeg|OpenCv|LibVLC|libvlc|SIPSorcery)/') {
            throw "Unexpected private or native package: $($library.Name)"
        }
    }
    foreach ($target in $assets.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            if ($library.Value.native) { throw "Native assets in $($library.Name)" }
            foreach ($asset in $library.Value.runtimeTargets.PSObject.Properties) {
                if ($asset.Value.assetType -eq 'native') { throw "Native runtime asset in $($library.Name)" }
            }
        }
    }
}
Write-Output 'Managed dependency audit passed: no private package, native asset, P/Invoke or external process entry point.'
