param([switch]$IncludeWindow)
$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
Push-Location $atlasRoot
try {
    & python scripts/sync-architecture.py --check
    if ($LASTEXITCODE -ne 0) { throw 'Architecture Markdown and application resource differ.' }
    & python scripts/sync-likec4.py --check
    if ($LASTEXITCODE -ne 0) { throw 'LikeC4 target model and JSON projection differ.' }
    & dotnet build CodeAtlas.slnx --artifacts-path artifacts/validation --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet artifacts/validation/bin/CodeAtlas.Regression/debug/CodeAtlas.Regression.dll . > artifacts/core-regression-desktop.json
    if ($LASTEXITCODE -ne 0) { throw 'Core regression failed.' }
    & dotnet artifacts/validation/bin/CodeAtlas.Cli/debug/CodeAtlas.Cli.dll check --root . --id desktop-self --name 'Code Atlas - WPF self analysis' --reports artifacts/reports --warnings-baseline docs/quality/self-warning-baseline.json > artifacts/desktop-self-check.json
    if ($LASTEXITCODE -ne 0) { throw 'Self analysis found a rule error. See artifacts/desktop-self-check.json.' }
    & dotnet artifacts/validation/bin/CodeAtlas.Desktop.Regression/debug/CodeAtlas.Desktop.Regression.dll artifacts/reports/desktop-self.json > artifacts/desktop-regression.json
    if ($LASTEXITCODE -ne 0) { throw 'Desktop state regression failed.' }
    if ($IncludeWindow) {
        & pwsh -NoProfile -File tests/desktop-window-check.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Native window verification failed.' }
    }
    Write-Host 'Passed. Coverage limitations and documentation gaps remain visible in the report.'
} finally { Pop-Location }
