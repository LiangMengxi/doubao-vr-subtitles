$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path $PSScriptRoot -Parent
$repositoryRoot = Split-Path $rewriteRoot -Parent
$backupRoot = Join-Path $rewriteRoot ('backups\source-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backupRoot | Out-Null
$sourceRoots = @((Join-Path $rewriteRoot 'core'),(Join-Path $rewriteRoot 'ui'),(Join-Path $rewriteRoot 'assets'),$PSScriptRoot)
if (Test-Path -LiteralPath (Join-Path $rewriteRoot 'tests')) { $sourceRoots += Join-Path $rewriteRoot 'tests' }
$files = Get-ChildItem -LiteralPath $sourceRoots -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
if (Test-Path -LiteralPath (Join-Path $rewriteRoot 'installer')) { $files += Get-ChildItem -LiteralPath (Join-Path $rewriteRoot 'installer') -Recurse -File }
$files += Get-ChildItem -LiteralPath $rewriteRoot -File
$rootFiles = @('.gitattributes','.gitignore','LICENSE','README.md','docs\RELEASE_NOTES.md','docs\RELEASING.md','docs\REFERENCES.md','docs\SOURCE_BUILD.md','THIRD_PARTY_NOTICES.md','docs\USAGE.md','VERSION','scripts\build.ps1','scripts\package-release.ps1','scripts\start.cmd','licenses\README.md','licenses\InnoSetup-LICENSE.txt','licenses\OpenVR-LICENSE.txt')
$files += $rootFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $repositoryRoot $_) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open((Join-Path $backupRoot 'source.zip'), [IO.Compression.ZipArchiveMode]::Create)
$manifest = @()
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($repositoryRoot.Length + 1)
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative.Replace('\','/')) | Out-Null
        $manifest += [pscustomobject]@{Path=$relative; SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
    }
} finally { $archive.Dispose() }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot 'source-sha256.json') -Encoding utf8
Get-FileHash -LiteralPath (Join-Path $backupRoot 'source.zip') -Algorithm SHA256 | Format-List
Write-Host "Source backup: $backupRoot"
