param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
& $Python -c "import sys; assert (3,10) <= sys.version_info[:2] < (3,14), 'Python 3.10-3.13 required'"
if($LASTEXITCODE -ne 0) { throw 'Select Python 3.10-3.13 using -Python C:\path\python.exe' }
& $Python -m venv (Join-Path $projectDir '.venv')
if($LASTEXITCODE -ne 0) { throw 'venv creation failed' }
& (Join-Path $projectDir '.venv\Scripts\python.exe') -m pip install -r (Join-Path $projectDir 'requirements.txt')
if($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
