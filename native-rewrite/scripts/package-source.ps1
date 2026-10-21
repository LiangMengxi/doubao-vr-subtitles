param([Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$destination = [IO.Path]::GetFullPath($ArchivePath)
if (-not $destination.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $destination)) { throw 'Source archive must be a new file inside the repository.' }
. (Join-Path $PSScriptRoot 'source-files.ps1')
$files = @(Get-PublicSourceFiles)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($destination, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($repositoryRoot.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "Corresponding source archive: $destination ($($files.Count) files; no private tests, reports or configuration)."
