param([string]$BuildRoot = 'artifacts/validation/bin', [string]$Report = 'artifacts/reports/desktop-self.json')
$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
Push-Location $atlasRoot
$atlasProcess = $null
try {
    $atlasGui = Join-Path $atlasRoot "$BuildRoot/CodeAtlas.Desktop/debug/CodeAtlas.Desktop.exe"
    $atlasCli = Join-Path $atlasRoot "$BuildRoot/CodeAtlas.Cli/debug/CodeAtlas.Cli.exe"
    $atlasReport = Join-Path $atlasRoot $Report
    $atlasOutput = Join-Path $atlasRoot 'artifacts/wpf-verification'
    New-Item -ItemType Directory -Force -Path $atlasOutput | Out-Null
    $atlasSession = 'qa-' + $PID
    $atlasChecks = [System.Collections.Generic.List[string]]::new()
    function Check([bool]$Condition, [string]$Message) {
        if (-not $Condition) { throw "FAIL: $Message" }
        $atlasChecks.Add($Message)
    }
    function Ui([string[]]$Arguments) {
        $atlasRaw = & $atlasCli ui @Arguments --session $atlasSession
        $atlasResult = ($atlasRaw -join "`n") | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $atlasResult.Ok) { throw ($atlasRaw -join "`n") }
        return $atlasResult.Result
    }
    $atlasStart = [Diagnostics.ProcessStartInfo]::new($atlasGui)
    $atlasStart.WorkingDirectory = $atlasRoot
    $atlasGuiReports = Join-Path $atlasOutput 'gui-reports'
    foreach ($atlasArgument in @('--report', $atlasReport, '--reports', $atlasGuiReports, '--session', $atlasSession)) { $atlasStart.ArgumentList.Add($atlasArgument) }
    $atlasProcess = [Diagnostics.Process]::Start($atlasStart)
    $atlasState = $null
    for ($atlasAttempt = 0; $atlasAttempt -lt 30; $atlasAttempt++) {
        try { $atlasState = Ui @('status'); if ($atlasState.report -and -not $atlasState.busy) { break } } catch { if ($atlasProcess.HasExited) { throw } }
        Start-Sleep -Milliseconds 200
    }
    Check ($null -ne $atlasState.report -and -not $atlasState.busy) 'native desktop loads a real report'
    Ui @('capture','--out', (Join-Path $atlasOutput 'overview.png')) | Out-Null
    $atlasSnapshot = Get-Content -LiteralPath $atlasReport -Raw | ConvertFrom-Json
    $atlasMethod = $atlasSnapshot.Graph.Nodes | Where-Object { $_.Name -eq 'ReadAsync' -and $_.Source.Path -eq 'src/CodeAtlas.Storage/SourceRepository.cs' } | Select-Object -First 1
    $atlasState = Ui @('select','--id','atlas.analysis')
    $atlasState = Ui @('select','--id',$atlasMethod.Id)
    Check ($atlasState.selected.Source.Line -eq $atlasMethod.Source.Line) 'CLI selection uses the exact source line'
    Check ($atlasState.currentModule -eq 'feature:atlas.analysis') 'method location preserves root module context'
    $atlasState = Ui @('undo'); Check ($atlasState.selected.Id -eq 'feature:atlas.analysis') 'native undo restores selection'
    $atlasState = Ui @('redo'); Check ($atlasState.selected.Id -eq ('node:' + $atlasMethod.Id)) 'native redo restores method'
    Ui @('capture','--out',(Join-Path $atlasOutput 'method.png')) | Out-Null
    $atlasMatches = Ui @('search','--query','ReadAsync'); Check ($atlasMatches.results.Count -ge 2) 'CLI search exposes implementation and contract separately'
    Ui @('search','--query','') | Out-Null
    $atlasBefore = Ui @('status')
    Ui @('scroll','--offset','0') | Out-Null
    $atlasState = Ui @('scroll','--offset','1000000')
    Check ($atlasState.currentModule -ne 'feature:atlas.analysis') 'actual scrolling updates minimap current module'
    Check ($atlasState.history -eq $atlasBefore.history) 'scrolling does not add history entries'
    foreach ($atlasModule in $atlasState.modules | Where-Object { $_.Id -like 'feature:*' }) {
        $atlasLocated = Ui @('locate','--id',$atlasModule.Id)
        Check ($atlasLocated.selected.Id -eq $atlasModule.Id) ('locate root ' + $atlasModule.Id)
    }
    Ui @('view','--mode','graph') | Out-Null
    Ui @('capture','--out',(Join-Path $atlasOutput 'graph.png')) | Out-Null
    $atlasState = Ui @('locate','--id',$atlasMethod.Id); Check ($atlasState.View -eq 'tree') 'graph selection returns to the correct directory'
    $atlasState = Ui @('window','--width','1100','--height','760'); Check ($atlasState.window.compact) 'narrow desktop uses compact layout'
    Ui @('capture','--out',(Join-Path $atlasOutput 'compact.png')) | Out-Null
    $atlasState = Ui @('window','--width','1440','--height','900','--detail-width','610')
    Check ([Math]::Abs($atlasState.window.detailWidth - 610) -lt 2) 'details can widen without replacing the directory'
    Check ($atlasState.window.sourceWrap -eq 'Wrap' -and $atlasState.window.sourceHorizontalScroll -eq 'Disabled') 'source wraps and never requires horizontal scrolling'
    $atlasSelection = $atlasState.selected.Id
    Ui @('window','--width','1100','--height','760') | Out-Null
    $atlasState = Ui @('window','--width','1440','--height','900')
    Check ([Math]::Abs($atlasState.window.detailWidth - 610) -lt 2 -and $atlasState.selected.Id -eq $atlasSelection) 'wide panel preference and selection survive compact mode'
    $atlasBad = & $atlasCli ui window --width 1440 --height 900 --detail-width 100 --session $atlasSession
    Check ($LASTEXITCODE -ne 0) 'invalid panel width is rejected'
    $atlasBad = & $atlasCli ui unsupported-action --session $atlasSession
    Check ($LASTEXITCODE -ne 0) 'unknown GUI actions fail explicitly'
    $atlasBad = & $atlasCli ui open --report (Join-Path $atlasOutput 'missing.json') --session $atlasSession
    Check ($LASTEXITCODE -ne 0) 'failed open reports an error'
    $atlasState = Ui @('status'); Check ($atlasState.report -eq $atlasSnapshot.Id) 'failed open preserves prior report'
    $atlasNoop = & $atlasCli unsupported-command
    Check ($LASTEXITCODE -eq 64) 'unknown headless command returns 64'
    $atlasFixture = Join-Path $atlasOutput 'fixture'
    New-Item -ItemType Directory -Force -Path $atlasFixture | Out-Null
    @'
using CodeAtlas.Framework;
using System.Windows.Controls;
[assembly: AtlasModule("parity", "WPF parity", ArchitecturalRole.Presentation)]
[assembly: AtlasFeature("parity", "WPF parity", "Check host-independent binding.", Kind = FeatureKind.Module)]
/// <summary>A native view with the same semantics in GUI and CLI.</summary>
public sealed class NativeView : UserControl
{
    /// <summary>Read the visible content label.</summary>
    /// <returns>The content text, or an empty string.</returns>
    public string Label() => Content?.ToString() ?? "";
}
'@ | Set-Content -LiteralPath (Join-Path $atlasFixture 'Fixture.cs') -Encoding utf8
    $atlasCliReports = Join-Path $atlasOutput 'cli-reports'
    & $atlasCli analyze --root $atlasFixture --id parity --reports $atlasCliReports | Out-Null
    Check ($LASTEXITCODE -eq 0) 'headless analysis of native WPF types succeeds'
    Ui @('analyze','--root',$atlasFixture) | Out-Null
    for ($atlasAttempt = 0; $atlasAttempt -lt 100; $atlasAttempt++) {
        $atlasState = Ui @('status'); if (-not $atlasState.busy) { break }; Start-Sleep -Milliseconds 100
    }
    Check (-not $atlasState.busy -and -not $atlasState.Error -and $atlasState.report -ne $atlasSnapshot.Id) 'GUI invokes the real shared analyzer asynchronously'
    $atlasCliReport = Get-Content -LiteralPath (Join-Path $atlasCliReports 'parity.json') -Raw | ConvertFrom-Json
    $atlasGuiReport = Get-Content -LiteralPath (Join-Path $atlasGuiReports ($atlasState.report + '.json')) -Raw | ConvertFrom-Json
    $atlasCliGraph = $atlasCliReport.Graph | ConvertTo-Json -Depth 80 -Compress
    $atlasGuiGraph = $atlasGuiReport.Graph | ConvertTo-Json -Depth 80 -Compress
    Check ($atlasCliGraph -ceq $atlasGuiGraph) 'GUI and CLI produce identical WPF symbols, relations and limitations'
    [ordered]@{ passed = $true; assertions = $atlasChecks.Count; tests = $atlasChecks } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $atlasOutput 'results.json') -Encoding utf8
    Write-Output "PASS: $($atlasChecks.Count) native GUI/CLI checks."
} finally {
    if ($atlasProcess -and -not $atlasProcess.HasExited) { $atlasProcess.CloseMainWindow() | Out-Null; $atlasProcess.WaitForExit(3000) | Out-Null }
    Pop-Location
}
