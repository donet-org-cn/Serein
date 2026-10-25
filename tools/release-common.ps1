function Assert-ReleaseVersion {
    param([Parameter(Mandatory = $true)][string]$Version)
    $pattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    if ($Version.Length -gt 100 -or $Version -cnotmatch $pattern) {
        throw 'Version must be MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-prerelease, without a v prefix.'
    }
    $prerelease = $Matches[4]
    if ($prerelease) {
        foreach ($identifier in $prerelease.Split('.')) {
            if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier.StartsWith('0')) {
                throw 'Numeric prerelease identifiers must not contain leading zeros.'
            }
        }
    }
}

function Get-DefaultReleaseVersion {
    $root = Split-Path -Parent $PSScriptRoot
    [xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Encoding UTF8 -Raw
    $version = [string]$properties.Project.PropertyGroup.Version
    Assert-ReleaseVersion -Version $version
    return $version
}
