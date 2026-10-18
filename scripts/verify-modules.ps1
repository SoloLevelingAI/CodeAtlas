param([switch]$Browser, [switch]$SkipNuGetAudit, [int]$Port = 5288, [int]$HeadlessPort = 5289)
$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
$previousResolver = $env:MSBuildEnableWorkloadResolver
$previousUrl = $env:ATLAS_URL
$previousReport = $env:ATLAS_REPORT
$previousLegacy = $env:ATLAS_LEGACY_REPORT
$previousHeadless = $env:ATLAS_HEADLESS_URL
$hosts = @()
Push-Location $atlasRoot
try {
    New-Item -ItemType Directory -Path artifacts/module-verification/reports -Force | Out-Null
    $verification = Join-Path $atlasRoot 'artifacts/module-verification'
    $reportPath = Join-Path $verification 'reports'
    $originals = @(Get-ChildItem artifacts/reports -Filter '*.json' -ErrorAction SilentlyContinue | Get-FileHash)
    New-Item -ItemType Directory -Path "$verification/original-reports" -Force | Out-Null
    foreach ($original in $originals) { Copy-Item -LiteralPath $original.Path -Destination "$verification/original-reports" }
    foreach ($original in $originals) { Copy-Item -LiteralPath $original.Path -Destination $reportPath }
    Copy-Item -LiteralPath tests/fixtures/legacy-no-features.json -Destination $reportPath
    python scripts/sync-architecture.py --check
    if ($LASTEXITCODE -ne 0) { throw 'Markdown resource check failed.' }
    $env:MSBuildEnableWorkloadResolver = 'false'
    $buildArguments = @('build', 'CodeAtlas.slnx', '-c', 'Release', '--nologo', '-v', 'minimal', '-m:1')
    if ($SkipNuGetAudit) { $buildArguments += '-p:NuGetAudit=false' }
    & dotnet @buildArguments | Tee-Object -FilePath "$verification/build.log"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet tests/CodeAtlas.Regression/bin/Release/net10.0/CodeAtlas.Regression.dll $atlasRoot | Tee-Object -FilePath "$verification/core-regression.json"
    if ($LASTEXITCODE -ne 0) { throw 'Core/module regression failed.' }
    dotnet tests/CodeAtlas.Desktop.Regression/bin/Release/net10.0/CodeAtlas.Desktop.Regression.dll | Tee-Object -FilePath "$verification/desktop-regression.json"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop state regression failed.' }
    $hostDll = Join-Path $atlasRoot 'src/CodeAtlas.Host/bin/Release/net10.0/CodeAtlas.Host.dll'
    dotnet $hostDll check --root $atlasRoot --id self --name 'CodeAtlas v0.2 self' --reports $reportPath | Tee-Object -FilePath "$verification/self-check.json"
    if ($LASTEXITCODE -ne 0) { throw 'Self analysis failed.' }
    if ($Browser) {
        $env:ATLAS_URL = "http://127.0.0.1:$Port"
        $env:ATLAS_HEADLESS_URL = "http://127.0.0.1:$HeadlessPort"
        $env:ATLAS_REPORT = 'self'
        $env:ATLAS_LEGACY_REPORT = 'legacy-no-features'
        foreach ($profile in @('web', 'headless')) {
            $url = if ($profile -eq 'web') { $env:ATLAS_URL } else { $env:ATLAS_HEADLESS_URL }
            try { $null = Invoke-WebRequest $url -TimeoutSec 1; throw "Port already serves an application: $url" }
            catch { if ($_.Exception.Message -like 'Port already*') { throw } }
            $arguments = @('"' + $hostDll + '"', 'serve', '--reports', '"' + $reportPath + '"', '--urls', $url, '--profile', $profile)
            $hosts += Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WorkingDirectory $atlasRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput "$verification/$profile-host.log" -RedirectStandardError "$verification/$profile-host-error.log"
            $ready = $false
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                try { $null = Invoke-WebRequest $url -TimeoutSec 1; $ready = $true; break } catch { Start-Sleep -Milliseconds 250 }
            }
            if (!$ready) { throw "Host failed to become ready: $url" }
        }
        foreach ($test in @('module', 'structure', 'focus-map', 'labor', 'browser')) {
            & node "tests/$test-check.cjs" | Tee-Object -FilePath "$verification/$test-browser.log"
            if ($LASTEXITCODE -ne 0) { throw "Browser regression failed: $test" }
        }
    }
    foreach ($original in $originals) {
        if ((Get-FileHash -LiteralPath $original.Path).Hash -ne $original.Hash) { throw "Original report changed: $($original.Path)" }
    }
    Write-Output "PASS: modules, compatibility, original report preservation. Evidence: $verification"
} finally {
    foreach ($hostProcess in $hosts) { if (!$hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id } }
    $env:MSBuildEnableWorkloadResolver = $previousResolver
    $env:ATLAS_URL = $previousUrl
    $env:ATLAS_REPORT = $previousReport
    $env:ATLAS_LEGACY_REPORT = $previousLegacy
    $env:ATLAS_HEADLESS_URL = $previousHeadless
    Pop-Location
}
