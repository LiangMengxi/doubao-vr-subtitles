param([switch]$Toolchain)
$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path $PSScriptRoot -Parent
$dependencyRoot = Join-Path $rewriteRoot 'dependencies'
$manifest = Get-Content -LiteralPath (Join-Path $rewriteRoot 'dependencies.lock.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null

function Get-VerifiedFile($url, $destination, $expectedHash) {
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $expectedHash) { return }
        throw "Dependency checksum mismatch: $destination"
    }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Write-Host ('Downloading ' + (Split-Path $destination -Leaf))
    $temporary = $destination + '.partial'
    Invoke-WebRequest -Uri $url -OutFile $temporary -UseBasicParsing
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expectedHash) { throw "Dependency checksum mismatch: $url" }
    Move-Item -LiteralPath $temporary -Destination $destination
}

foreach ($entry in $manifest.sdkFiles) {
    Get-VerifiedFile $entry.url (Join-Path $dependencyRoot $entry.path) $entry.sha256
}
if (-not $Toolchain) { return }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($entry in $manifest.toolchainArchives) {
    $archivePath = Join-Path $dependencyRoot ('downloads\' + $entry.name)
    Get-VerifiedFile $entry.url $archivePath $entry.sha256
    $stampPath = Join-Path $dependencyRoot ('expanded\' + $entry.sha256 + '.txt')
    if (Test-Path -LiteralPath $stampPath) { continue }
    $targetRoot = [IO.Path]::GetFullPath((Join-Path $dependencyRoot $entry.target))
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($item in $archive.Entries) {
            $name = $item.FullName.Replace('\', '/')
            if (-not $name.StartsWith($entry.prefix) -or $name.EndsWith('/')) { continue }
            $relative = $name.Substring($entry.prefix.Length)
            $destination = [IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
            if (-not $destination.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe archive entry: $name" }
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            $inputStream = $item.Open()
            try {
                $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
                try { $entryHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash($inputStream)).Replace('-', '') }
                finally { $hashAlgorithm.Dispose() }
            } finally { $inputStream.Dispose() }
            if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $entryHash) { continue }
            $inputStream = $item.Open()
            try {
                $outputStream = [IO.File]::Create($destination)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        }
    } finally { $archive.Dispose() }
    New-Item -ItemType Directory -Path (Split-Path $stampPath -Parent) -Force | Out-Null
    Set-Content -LiteralPath $stampPath -Value $entry.sha256 -Encoding ascii
}
Write-Host 'Local toolchain prepared. No system installation or environment changes.'
