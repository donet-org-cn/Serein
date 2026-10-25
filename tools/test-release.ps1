$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$valid = @('0.1.0', '1.2.3', '0.1.0-preview.1', '2.0.0-rc.0', '1.0.0-beta-2')
$invalid = @('v1.0.0', '1.0', '01.0.0', '1.0.0-preview.01', '1.0.0+build', '../1.0.0', '1.0.0;echo unsafe', "1.0.0`nunsafe")
foreach ($version in $valid) { Assert-ReleaseVersion -Version $version }
foreach ($version in $invalid) {
    $rejected = $false
    try { Assert-ReleaseVersion -Version $version }
    catch { $rejected = $true }
    if (!$rejected) { throw "Invalid release version accepted: $version" }
}
Write-Output "Release version checks passed ($($valid.Count + $invalid.Count) cases)."
