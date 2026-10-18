# Run with Windows PowerShell in an interactive desktop, after starting the local trial app.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AtlasPointer {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@
$atlasRoot = Split-Path $PSScriptRoot -Parent
$atlasCli = Join-Path $atlasRoot 'artifacts/desktop/cli/CodeAtlas.Cli.exe'
$atlasOut = Join-Path $atlasRoot 'artifacts/wpf-verification'
$atlasChecks = New-Object 'System.Collections.Generic.List[string]'
function Check($condition, [string]$message) { if (-not $condition) { throw $message }; $atlasChecks.Add($message) }
function Ui([string[]]$arguments) {
    $raw = & $atlasCli ui @arguments
    $result = ($raw -join "`n") | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $result.Ok) { throw ($raw -join "`n") }
    return $result.Result
}
function Find($root, [string]$name) {
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$name)))
}
function Invoke($element) {
    if ($null -eq $element) { throw 'Missing control' }
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 220
}
function Guide {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $windows = $atlasMain.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window)))
        $found = $windows | Where-Object { $_.Current.Name -like '架构规范*' } | Select-Object -First 1
        if ($found) { return $found }
        Start-Sleep -Milliseconds 100
    }
}
$atlasProcess = Get-Process CodeAtlas.Desktop | Where-Object { $_.Path -like '*artifacts\desktop\gui*' } | Select-Object -First 1
if (-not $atlasProcess) { throw 'Start the local trial WPF window first.' }
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    $atlasProcess.Refresh()
    if ($atlasProcess.MainWindowHandle -ne [IntPtr]::Zero) {
        try { $atlasReady = Ui @('status'); if ($atlasReady.report -and -not $atlasReady.busy) { break } } catch { }
    }
    Start-Sleep -Milliseconds 200
}
$atlasMain = [Windows.Automation.AutomationElement]::FromHandle($atlasProcess.MainWindowHandle)
$atlasMain.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window))) | Where-Object { $_.Current.Name -like '架构规范*' } | ForEach-Object { $_.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
[AtlasPointer]::SetForegroundWindow($atlasProcess.MainWindowHandle) | Out-Null
Invoke (Find $atlasMain '架构规范')
$atlasGuide = Guide
Check ($null -ne $atlasGuide) 'native architecture window opens'
$atlasA03 = Find $atlasGuide 'A03  页面交互有自己的边界'
$atlasA03.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 220
$atlasButtons = $atlasGuide.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)))
$atlasEvidence = $atlasButtons | Where-Object { $_.Current.Name -like 'LD002*' } | Select-Object -First 1
Check ($null -ne $atlasEvidence) 'LaunchDesk ViewModel violation is visible as real evidence'
[AtlasPointer]::SetForegroundWindow([IntPtr]$atlasGuide.Current.NativeWindowHandle) | Out-Null
Start-Sleep -Milliseconds 250
$atlasRect = $atlasGuide.Current.BoundingRectangle
$atlasBitmap = New-Object Drawing.Bitmap([int]$atlasRect.Width,[int]$atlasRect.Height)
$atlasGraphics = [Drawing.Graphics]::FromImage($atlasBitmap)
try {
    $atlasGraphics.CopyFromScreen([int]$atlasRect.X,[int]$atlasRect.Y,0,0,$atlasBitmap.Size)
    $atlasBitmap.Save((Join-Path $atlasOut 'launchdesk-architecture.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $atlasGraphics.Dispose(); $atlasBitmap.Dispose() }
Invoke $atlasEvidence
$atlasState = Ui @('status')
Check ($atlasState.selected.Title -eq 'LaunchAsync' -and $atlasState.selected.Source.Line -eq 240) 'architecture evidence returns to exact method and original line'
Invoke (Find $atlasMain '架构规范')
$atlasGuide = Guide
(Find $atlasGuide 'A07  主干与可选模块能真正拆装').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 180
Check ($null -ne (Find $atlasGuide '需要人工验证 / 自动检查未实现')) 'unsupported module checks are never shown as passing'
$atlasGuide.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
[AtlasPointer]::SetForegroundWindow($atlasProcess.MainWindowHandle) | Out-Null
Ui @('window','--width','1440','--height','900','--detail-width','530') | Out-Null
$atlasBefore = Ui @('status')
$atlasSplitter = Find $atlasMain '调整目录与源码宽度'
Check ($null -ne $atlasSplitter) 'native draggable divider is accessible'
$atlasRect = $atlasSplitter.Current.BoundingRectangle
$atlasX = [int]($atlasRect.X + $atlasRect.Width / 2); $atlasY = [int]($atlasRect.Y + $atlasRect.Height / 2)
[AtlasPointer]::SetCursorPos($atlasX,$atlasY) | Out-Null
[AtlasPointer]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
try { for ($i=1; $i -le 12; $i++) { [AtlasPointer]::SetCursorPos(($atlasX-$i*10),$atlasY) | Out-Null; Start-Sleep -Milliseconds 25 } }
finally { [AtlasPointer]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
Start-Sleep -Milliseconds 200
$atlasAfter = Ui @('status')
Check ($atlasAfter.window.detailWidth -gt $atlasBefore.window.detailWidth + 30) 'real mouse drag widens source panel'
Check ($atlasAfter.selected.Id -eq $atlasBefore.selected.Id -and $atlasAfter.history -eq $atlasBefore.history) 'drag preserves selection and navigation history'
Check ($atlasAfter.window.sourceWrap -eq 'Wrap' -and $atlasAfter.window.sourceHorizontalScroll -eq 'Disabled') 'actual native source view wraps without horizontal scroll'
Ui @('capture','--out',(Join-Path $atlasOut 'launchdesk-desktop.png')) | Out-Null
@{passed=$true; checks=$atlasChecks.Count; tests=$atlasChecks} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $atlasOut 'launchdesk-reading-results.json') -Encoding UTF8
Write-Output "PASS: $($atlasChecks.Count) actual architecture and divider checks."
