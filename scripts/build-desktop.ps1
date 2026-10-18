param([string]$Output = 'artifacts/desktop')
$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path $PSScriptRoot -Parent
Push-Location $atlasRoot
try {
    & python scripts/sync-architecture.py --check
    if ($LASTEXITCODE -ne 0) { throw 'Architecture Markdown and application resource differ.' }
    & python scripts/sync-likec4.py --check
    if ($LASTEXITCODE -ne 0) { throw 'LikeC4 target model and JSON projection differ.' }
    $atlasOutput = [IO.Path]::GetFullPath($Output)
    foreach ($atlasTarget in @(@('CodeAtlas.Desktop', 'gui'), @('CodeAtlas.Cli', 'cli'))) {
        & dotnet publish "src/$($atlasTarget[0])/$($atlasTarget[0]).csproj" -c Release --self-contained false -o (Join-Path $atlasOutput $atlasTarget[1]) --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($atlasTarget[0])" }
    }
    Write-Host "Desktop: $atlasOutput/gui/CodeAtlas.Desktop.exe"
    Write-Host "CLI:     $atlasOutput/cli/CodeAtlas.Cli.exe"
    Write-Host 'Requires .NET 10 Desktop Runtime. Outputs stay local and are excluded from Git.'
} finally { Pop-Location }
