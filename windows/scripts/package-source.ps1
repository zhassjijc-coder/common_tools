param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/LinkLab.App/LinkLab.App.csproj') -Raw)).Project.PropertyGroup.Version
$artifactDir = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
$archivePath = Join-Path $artifactDir "LinkLab-$appVersion-source.zip"
$rootFiles = @('.gitignore', 'README.md', 'README.en.md', 'THIRD-PARTY-NOTICES.md', 'Directory.Build.props', 'global.json', 'LinkLab.sln', 'NuGet.Config', 'LICENSE', 'LICENSE.md', 'LICENSE.txt')
$files = @(Get-ChildItem -LiteralPath $projectRoot -File | Where-Object { $_.Name -in $rootFiles })
foreach ($directory in @('src', 'tests', 'scripts', 'licenses', 'docs')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\', '/')
            if ($relative.StartsWith('../')) { throw 'Source archive path escaped the project.' }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "LinkLab-source/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach($name in @('.gitignore', '.gitattributes')) {
            $shared = Join-Path (Split-Path -Parent $projectRoot) $name
            if(Test-Path -LiteralPath $shared) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $shared, "LinkLab-source/$name", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Get-Item -LiteralPath $archivePath | Select-Object FullName, Length
