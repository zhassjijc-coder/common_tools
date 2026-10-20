param([string]$Executable = '', [string]$Output = 'artifacts/ui-smoke')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) {
    $appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/LinkLab.App/LinkLab.App.csproj') -Raw)).Project.PropertyGroup.Version
    $Executable = "artifacts/LinkLab-$appVersion-win-x64/LinkLab.exe"
}
$exePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Executable))
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (-not (Test-Path -LiteralPath $exePath)) { throw 'Build or publish the app first.' }
$testExe = Join-Path $projectRoot 'tests\LinkLab.Tests\bin\Release\net10.0\LinkLab.Tests.exe'
if (-not (Test-Path -LiteralPath $testExe)) { throw 'Build Release tests first; UI smoke uses their local MQTT Broker.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
$oldPort = $env:LINKLAB_SMOKE_MQTT_PORT
$env:LINKLAB_SMOKE_MQTT_PORT = $port.ToString()
$brokerLog = Join-Path $outputPath 'broker.log'
$broker = Start-Process -FilePath $testExe -ArgumentList @('--broker', $port) -WindowStyle Hidden -PassThru -RedirectStandardOutput $brokerLog -RedirectStandardError (Join-Path $outputPath 'broker-error.log')
try {
    $ready = $false
    for ($i = 0; $i -lt 100; $i++) { Start-Sleep -Milliseconds 50; if ((Get-Content -LiteralPath $brokerLog -Raw -ErrorAction SilentlyContinue) -match 'READY') { $ready = $true; break }; if ($broker.HasExited) { break } }
    if (-not $ready) { throw 'Local MQTT smoke Broker failed to start.' }
    $process = Start-Process -FilePath $exePath -ArgumentList @('--smoke-test', ('"' + $outputPath + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "UI smoke failed (exit $($process.ExitCode)). See $outputPath." }
    Get-Content -LiteralPath (Join-Path $outputPath 'result.txt')
    Get-Content -LiteralPath (Join-Path $outputPath 'extended-result.txt')
    Get-Content -LiteralPath (Join-Path $outputPath 'window-close-result.txt')
    Get-Content -LiteralPath (Join-Path $outputPath 'system-settings-result.txt')
    Get-Content -LiteralPath (Join-Path $outputPath 'virtual-serial-result.txt')
    Get-Content -LiteralPath (Join-Path $outputPath 'navigation-scroll-result.txt')
    Get-Content -LiteralPath (Join-Path $outputPath 'network-configuration-result.txt')
} finally {
    if (-not $broker.HasExited) { Stop-Process -Id $broker.Id }
    $env:LINKLAB_SMOKE_MQTT_PORT = $oldPort
}
