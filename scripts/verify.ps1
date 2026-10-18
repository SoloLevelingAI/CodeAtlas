$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
Push-Location $atlasRoot
try {
    dotnet build CodeAtlas.slnx --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    dotnet tests/CodeAtlas.Regression/bin/Debug/net10.0/CodeAtlas.Regression.dll $atlasRoot | Tee-Object -FilePath artifacts/regression-results.json
    if ($LASTEXITCODE -ne 0) { throw 'Regression failed.' }
    dotnet src/CodeAtlas.Host/bin/Debug/net10.0/CodeAtlas.Host.dll check --root $atlasRoot --id self --name 'Code Atlas · 自身源码' --reports (Join-Path $atlasRoot 'artifacts/reports') | Tee-Object -FilePath artifacts/self-check.json
    if ($LASTEXITCODE -ne 0) { throw 'Self check failed.' }
} finally { Pop-Location }
