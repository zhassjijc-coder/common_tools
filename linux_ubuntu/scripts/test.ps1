$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$oldPythonPath=$env:PYTHONPATH
$oldQtPlatform=$env:QT_QPA_PLATFORM
try {
    $env:PYTHONPATH = Join-Path $projectDir 'src'
    $env:QT_QPA_PLATFORM = 'offscreen'
    $python = Join-Path $projectDir '.venv\Scripts\python.exe'
    & $python -m unittest discover -s (Join-Path $projectDir 'tests') -v
    if($LASTEXITCODE -ne 0) { throw 'Protocol/unit tests failed.' }
    & $python (Join-Path $projectDir 'main.py') --smoke-test (Join-Path $projectDir 'artifacts\ui-smoke')
    if($LASTEXITCODE -ne 0) { throw 'UI smoke failed.' }
} finally {
    $env:PYTHONPATH=$oldPythonPath
    $env:QT_QPA_PLATFORM=$oldQtPlatform
}
