param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet build LinkLab.sln -c Release --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($RunTests) {
        dotnet tests/LinkLab.Tests/bin/Release/net10.0/LinkLab.Tests.dll
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
} finally { Pop-Location }
