param([string]$RuntimeVersion = '10.0.13', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $appVersion = ([xml](Get-Content -LiteralPath src/LinkLab.App/LinkLab.App.csproj -Raw)).Project.PropertyGroup.Version
    $publishDir = Join-Path $projectRoot "artifacts/LinkLab-$appVersion-win-x64"
    [string[]]$restoreArgs = @()
    if ($NoRestore) { $restoreArgs += '--no-restore' }
    dotnet publish src/LinkLab.App/LinkLab.App.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -o $publishDir --nologo -m:1 @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath LICENSE -Destination (Join-Path $publishDir 'LICENSE') -Force
    Copy-Item -LiteralPath README.md -Destination (Join-Path $publishDir 'README.md') -Force
    Copy-Item -LiteralPath README.en.md -Destination (Join-Path $publishDir 'README.en.md') -Force
    $docsDir = Join-Path $publishDir 'docs'
    New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
    Copy-Item -LiteralPath docs/images -Destination $docsDir -Recurse -Force
    Get-ChildItem -Path docs/*.md -File | Copy-Item -Destination $docsDir -Force
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $publishDir 'THIRD-PARTY-NOTICES.md') -Force
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $licenseDir = Join-Path $publishDir 'licenses'
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File | Copy-Item -Destination $licenseDir -Force
    foreach ($component in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        foreach ($file in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $source = Join-Path $packageRoot "$component\$RuntimeVersion\$file"
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $licenseDir "$component-$file") -Force }
        }
    }
    $archivePath = "artifacts/LinkLab-$appVersion-win-x64.zip"
    Compress-Archive -LiteralPath $publishDir -DestinationPath $archivePath -Force
    Write-Output (Get-Item -LiteralPath $archivePath).FullName
} finally { Pop-Location }
