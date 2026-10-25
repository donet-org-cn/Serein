param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PackageDirectory,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
Assert-ReleaseVersion -Version $Version
if (!$PackageDirectory) { $PackageDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages' }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ids = @('Serein.Media', 'Serein.Rtsp', 'Serein.Hosting', 'Serein.AspNetCore')
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Filter '*.nupkg')
if ($packages.Count -ne $ids.Count) { throw 'Expected exactly four Serein NuGet packages.' }
$verified = @{}
foreach ($package in $packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $specifications = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
        if ($specifications.Count -ne 1) { throw "Invalid package manifest: $($package.Name)" }
        $reader = New-Object IO.StreamReader($specifications[0].Open())
        try { [xml]$specification = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $metadata = $specification.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $id = [string]$metadata.id
        if ($ids -cnotcontains $id -or $verified.ContainsKey($id)) { throw "Unexpected or duplicate package ID: $id" }
        if ([string]$metadata.version -cne $Version) { throw "Incorrect version in $($package.Name)" }
        if ($package.Name -cne "$id.$Version.nupkg") { throw "Unexpected package filename: $($package.Name)" }
        foreach ($dependency in $metadata.SelectNodes('.//*[local-name()="dependency"]')) {
            if ($dependency.id -like 'Serein.*' -and $dependency.version -notin @($Version, "[$Version]")) {
                throw "Mismatched Serein dependency version in $id"
            }
        }
        $verified[$id] = $package
    }
    finally { $archive.Dispose() }
}

foreach ($id in $ids) {
    $package = $verified[$id]
    $hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
    Write-Output "Verified $($package.Name) SHA256=$hash"
}
if ($DryRun) { Write-Output 'NuGet publish dry run passed; no package was uploaded.'; return }
if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    throw 'NuGet credentials are missing. Configure NUGET_API_KEY or NUGET_USER and a trusted publishing policy; see docs/publishing.md.'
}
foreach ($id in $ids) {
    dotnet nuget push $verified[$id].FullName --source 'https://api.nuget.org/v3/index.json' --api-key $env:NUGET_API_KEY --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "NuGet push failed for $id. Re-run this workflow to retry missing packages." }
}
