param([string]$Report, [string]$Session = 'default')
$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
if ($Session -notmatch '^[A-Za-z0-9_-]{1,50}$') { throw 'Invalid session name.' }
$atlasExe = Join-Path $atlasRoot 'artifacts/desktop/gui/CodeAtlas.Desktop.exe'
if (-not (Test-Path -LiteralPath $atlasExe)) { & "$PSScriptRoot/build-desktop.ps1" }
if (-not $Report) {
    foreach ($atlasCandidate in @('launchdesk-reading.json', 'desktop-self.json', 'self.json')) {
        $atlasPath = Join-Path $atlasRoot "artifacts/reports/$atlasCandidate"
        if (Test-Path -LiteralPath $atlasPath) { $Report = $atlasPath; break }
    }
}
$atlasArguments = @('--session', $Session, '--reports', ('"' + (Join-Path $atlasRoot 'artifacts/reports') + '"'))
if ($Report) {
    $atlasReport = (Resolve-Path -LiteralPath $Report).Path
    $atlasArguments += @('--report', ('"' + $atlasReport + '"'))
}
Start-Process -FilePath $atlasExe -ArgumentList $atlasArguments -WorkingDirectory $atlasRoot
