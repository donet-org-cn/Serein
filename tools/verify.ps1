param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (!$NoRestore) {
        dotnet restore Serein.slnx --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    }
    dotnet build Serein.slnx -c Release --no-restore --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    & (Join-Path $PSScriptRoot 'verify-managed.ps1')
    foreach ($suite in @('Serein.Media.Tests', 'Serein.Hosting.Tests')) {
        dotnet run --project "tests/$suite" -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
    }
    dotnet pack Serein.slnx -c Release --no-build --nologo -o artifacts/packages
    if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
}
finally { Pop-Location }
