param([switch]$Background)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$python = Join-Path $projectDir '.venv\Scripts\pythonw.exe'
if(-not (Test-Path -LiteralPath $python)) { throw 'Run setup.ps1 first.' }
$argsList = @(('"'+(Join-Path $projectDir 'main.py')+'"'))
if($Background) { $argsList += '--background' }
Start-Process -FilePath $python -ArgumentList $argsList -WorkingDirectory $projectDir -WindowStyle Hidden
