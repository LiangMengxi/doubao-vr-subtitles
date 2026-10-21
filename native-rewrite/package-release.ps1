param(
    [Parameter(Mandatory)][string]$Iscc,
    [string]$ReleaseDirectory = '',
    [switch]$SourceBuild
)
$ErrorActionPreference = 'Stop'
$rewriteRoot = $PSScriptRoot
$repositoryRoot = Split-Path $rewriteRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain three numeric components.' }
$compiler = Get-Item -LiteralPath $Iscc
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { $ReleaseDirectory = Join-Path $repositoryRoot $(if ($SourceBuild) { 'artifacts\source-build' } else { 'releases' }) }
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not $releaseRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output must stay inside the repository.' }
$formalReleases = Join-Path $repositoryRoot 'releases'
if ($SourceBuild -and ($releaseRoot -eq $formalReleases -or $releaseRoot.StartsWith($formalReleases + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw 'Source rebuilds must not replace formal release files.' }
& (Join-Path $rewriteRoot 'build.ps1') -Publish:(!$SourceBuild) -SourceBuild:$SourceBuild
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stage = Join-Path $rewriteRoot ('out\release-' + $stamp)
$payload = Join-Path $stage 'DoubaoVR'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$published = Join-Path $rewriteRoot $(if ($SourceBuild) { 'out\source-app' } else { 'out\app' })
$listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$allowedExtensions = @('.dll','.exe','.json','.xbf','.pri','.ico','.txt','.html','.winmd','.mui','.png','.md','.pdb')
foreach ($line in Get-Content -LiteralPath (Join-Path $published 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'Invalid publish manifest.' }
    $expectedHash = $Matches[1]
    $relative = $Matches[2]
    $source = [IO.Path]::GetFullPath((Join-Path $published $relative))
    if (-not $source.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish path.' }
    if (-not $listed.Add($relative)) { throw 'Duplicate publish manifest entry.' }
    if ([IO.Path]::GetExtension($source) -notin $allowedExtensions -or
        $relative -match '(^|[\\/])(settings[^\\/]*|logs|artifacts|backups|dependencies|\.env[^\\/]*)([\\/]|$)') { throw "Unexpected payload: $relative" }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $expectedHash) { throw "Publish checksum mismatch: $relative" }
    if ([IO.Path]::GetExtension($source) -in @('.pdb','.md')) { continue }
    $target = Join-Path $payload $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
    $relative = $file.FullName.Substring($published.Length + 1)
    if ($relative -ne 'SHA256SUMS.txt' -and -not $listed.Contains($relative)) { throw "Unlisted publish file: $relative" }
}
foreach ($document in @('VERSION')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $document) -Destination (Join-Path $payload $document)
}
Copy-Item -LiteralPath (Join-Path $rewriteRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $payload 'licenses\THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses\InnoSetup-LICENSE.txt') -Destination (Join-Path $payload 'licenses\InnoSetup-LICENSE.txt')
foreach ($name in @('DoubaoVR.exe','DoubaoVR.Core.dll')) {
    $file = Get-Item -LiteralPath (Join-Path $payload $name)
    if ($file.VersionInfo.ProductVersion -ne $version -or $file.VersionInfo.FileVersion -ne "$version.0") { throw "Incorrect application version: $name" }
}
foreach ($name in @('DoubaoVR.dll','DoubaoVR.deps.json','DoubaoVR.runtimeconfig.json','App.xbf','DoubaoVR.pri','openvr_api.dll','licenses\WindowsAppSDK-LICENSE.txt','licenses\DotNet-LICENSE.txt','licenses\GPL-3.0.txt','licenses\APPLICATION-NOTICE.txt','licenses\REFERENCES.txt','OpenVR-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Missing runtime file: $name" }
}
if (Test-Path -LiteralPath (Join-Path $payload 'DoubaoVR.Desktop.exe')) { throw 'Stale application executable.' }
if (Get-ChildItem -LiteralPath $payload -Recurse -File -Filter '*.md') { throw 'Release payload must not contain Markdown documents.' }
$verification = [Collections.Generic.List[string]]::new()
$verification.Add('procedure VerifyPayload;')
$verification.Add('begin')
$checksums = foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    $digest = (Get-FileHash -LiteralPath $file.FullName).Hash
    $escaped = $relative.Replace("'", "''")
    $verification.Add("  if not FileExists(ExpandConstant('{app}\$escaped')) then RaiseException('主程序文件不完整，请重新下载安装包。');")
    $verification.Add("  if CompareText(GetSHA256OfFile(ExpandConstant('{app}\$escaped')), '$digest') <> 0 then RaiseException('主程序校验失败，已取消启动。请重新下载安装包。');")
    "$digest  $relative"
}
$verification.Add('end;')
$verificationFile = Join-Path $stage 'VerifyPayload.iss'
$verification | Set-Content -LiteralPath $verificationFile -Encoding utf8
$checksums | Set-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt') -Encoding utf8
$archivePath = Join-Path $releaseRoot "DoubaoVR-v$version-windows-x64.zip"
$installerPath = Join-Path $releaseRoot "DoubaoVR-v$version-Setup-x64.exe"
$sourcePath = Join-Path $releaseRoot "DoubaoVR-v$version-source.zip"
foreach ($existing in @($archivePath,$installerPath,$sourcePath,($archivePath + '.sha256'),($installerPath + '.sha256'),($sourcePath + '.sha256'))) {
    if (Test-Path -LiteralPath $existing) {
        $backup = Join-Path $rewriteRoot ('out\release-backups\' + $stamp)
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Move-Item -LiteralPath $existing -Destination (Join-Path $backup (Split-Path $existing -Leaf))
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
& (Join-Path $rewriteRoot 'scripts\package-source.ps1') -ArchivePath $sourcePath
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $true)
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($archive.Entries.Count -ne $checksums.Count + 1) { throw 'ZIP entry count mismatch.' }
    foreach ($entry in $archive.Entries) {
        if (-not $entry.FullName.StartsWith('DoubaoVR/') -or $entry.FullName -match '\.md$|(^|/)(settings[^/]*|logs|artifacts|backups|dependencies|\.env[^/]*)(/|$)') { throw 'Unexpected ZIP entry.' }
        $stream = $entry.Open()
        try {
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
            if ($digest -ne (Get-FileHash -LiteralPath (Join-Path $stage $entry.FullName)).Hash) { throw 'ZIP content mismatch.' }
        } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }
& $compiler.FullName /Q "/DAppVersion=$version" "/DPayloadDir=$payload" "/DReleaseDir=$releaseRoot" "/DVerificationFile=$verificationFile" (Join-Path $rewriteRoot 'installer\DoubaoVR.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Get-Item -LiteralPath $installerPath
if ($installer.VersionInfo.ProductVersion.Trim() -ne $version -or $installer.VersionInfo.FileVersion.Trim() -ne "$version.0" -or $installer.VersionInfo.FileDescription.Trim() -ne '豆包VR字幕安装程序') { throw 'Incorrect installer identity/version.' }
foreach ($file in @($archivePath,$installerPath,$sourcePath)) {
    $digest = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    "$digest  $(Split-Path $file -Leaf)" | Set-Content -LiteralPath ($file + '.sha256') -Encoding ascii
    Write-Host "$file`nSHA256 $digest"
}
Write-Host "Verified $($checksums.Count) payload files; runtime contains no Markdown documents, personal settings, logs, development tools or debug symbols."
