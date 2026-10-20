param([string]$Executable = '', [string]$Output = 'artifacts/close-smoke')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) {
    $appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/LinkLab.App/LinkLab.App.csproj') -Raw)).Project.PropertyGroup.Version
    $Executable = "artifacts/LinkLab-$appVersion-win-x64/LinkLab.exe"
}
$exePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Executable))
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (-not (Test-Path -LiteralPath $exePath)) { throw 'Build or publish the app first.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$process = Start-Process -FilePath $exePath -ArgumentList @('--close-smoke-test', ('"' + $outputPath + '"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(45000)) {
    Stop-Process -Id $process.Id
    throw 'The owned close-test process timed out. No other LinkLab process was stopped.'
}
if ($process.ExitCode -ne 0) {
    $failurePath = Join-Path $outputPath 'failure.txt'
    if (Test-Path -LiteralPath $failurePath) { Get-Content -LiteralPath $failurePath }
    throw "Window close smoke failed (exit $($process.ExitCode))."
}
Get-Content -LiteralPath (Join-Path $outputPath 'window-close-result.txt')
