param([switch]$NoRestore, [string]$Version)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
if (!$Version) { $Version = Get-DefaultReleaseVersion }
Assert-ReleaseVersion -Version $Version
& (Join-Path $PSScriptRoot 'test-release.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (!$NoRestore) {
        dotnet restore Serein.slnx --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    }
    dotnet build Serein.slnx -c Release --no-restore --nologo -warnaserror "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    & (Join-Path $PSScriptRoot 'verify-managed.ps1')
    foreach ($suite in @('Serein.Media.Tests', 'Serein.Hosting.Tests')) {
        dotnet run --project "tests/$suite" -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
    }
    $packageDirectory = Join-Path $repoRoot 'artifacts/packages'
    if (Test-Path -LiteralPath $packageDirectory) {
        Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nupkg' |
            Where-Object { $_.Name -like 'Serein.*' } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    }
    dotnet pack Serein.slnx -c Release --no-build --nologo -o artifacts/packages "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
    & (Join-Path $PSScriptRoot 'publish-nuget.ps1') -Version $Version -PackageDirectory $packageDirectory -DryRun
}
finally { Pop-Location }
