$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $atlasRoot 'artifacts/reports/self.json'))) {
    & (Join-Path $PSScriptRoot 'verify.ps1')
}
Push-Location (Join-Path $atlasRoot 'src/CodeAtlas.Host')
try {
    dotnet bin/Debug/net10.0/CodeAtlas.Host.dll serve --reports (Join-Path $atlasRoot 'artifacts/reports') --urls http://127.0.0.1:5278
    if ($LASTEXITCODE -ne 0) { throw 'Local server failed.' }
} finally { Pop-Location }
