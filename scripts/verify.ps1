param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    & (Join-Path $PSScriptRoot 'check-localization.ps1')
    dotnet build CFLDetectools.sln --configuration $Configuration -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE."
    }

    dotnet run --project tests/CFLDetectools.Checks --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Behavior checks failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
