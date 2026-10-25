param([string]$Version, [string]$Tag, [switch]$Publish)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')

if ($Tag) {
    if (!$Tag.StartsWith('v', [StringComparison]::Ordinal)) { throw 'Release tags must begin with v.' }
    $tagVersion = $Tag.Substring(1)
    if ($Version -and $Version -cne $tagVersion) { throw 'The supplied version does not match the release tag.' }
    $Version = $tagVersion
}
elseif (!$Version) { $Version = Get-DefaultReleaseVersion }
Assert-ReleaseVersion -Version $Version

Write-Output "Release version: $Version; publish: $($Publish.IsPresent.ToString().ToLowerInvariant())"
if ($env:GITHUB_OUTPUT) {
    @("version=$Version", "publish=$($Publish.IsPresent.ToString().ToLowerInvariant())") |
        Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}
