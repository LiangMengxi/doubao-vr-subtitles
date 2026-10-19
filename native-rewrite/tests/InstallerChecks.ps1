param(
    [Parameter(Mandatory)][string]$Iscc,
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$VerificationFile,
    [Parameter(Mandatory)][string]$LegacyArchive
)
$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path $PSScriptRoot -Parent
$repositoryRoot = Split-Path $rewriteRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $rewriteRoot 'VERSION') -Raw).Trim()
$compiler = (Get-Item -LiteralPath $Iscc).FullName
$payload = (Get-Item -LiteralPath $PayloadDirectory).FullName
$verification = (Get-Item -LiteralPath $VerificationFile).FullName
$archivePath = (Get-Item -LiteralPath $LegacyArchive).FullName
$root = Join-Path $rewriteRoot ('out\installer-checks-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$install = Join-Path $root '中文 安装目录'
$legacy = Join-Path $root 'legacy'
New-Item -ItemType Directory -Path $legacy -Force | Out-Null
$productionId = '{F6AC8595-E870-5E91-9159-0B12C56E0E41}_is1'
$validationId = '{2B03D99A-765F-4A8F-A6B3-9A05CD6A0250}_is1'
$registryRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$productionKey = Join-Path $registryRoot $productionId
$validationKey = Join-Path $registryRoot $validationId
if (Test-Path -LiteralPath $validationKey) { throw 'An earlier isolated installer check still exists; inspect it before rerunning.' }
$originalInstall = Get-ItemProperty -LiteralPath $productionKey -ErrorAction SilentlyContinue
$profiles = foreach ($directory in @('DoubaoVR','DoubaoVR.Native')) {
    $path = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "$directory\settings.json"
    if (Test-Path -LiteralPath $path) { [pscustomobject]@{ Path = $path; Hash = (Get-FileHash -LiteralPath $path).Hash } }
}
function Assert-Profiles {
    foreach ($profile in $profiles) {
        if (-not (Test-Path -LiteralPath $profile.Path) -or (Get-FileHash -LiteralPath $profile.Path).Hash -ne $profile.Hash) { throw 'An existing user configuration changed during installation.' }
    }
}
function Run-Setup([string]$executable, [string[]]$arguments, [string]$label, [string]$expectedExecutable = '') {
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if ($expectedExecutable -ne '' -and $process.Path -ne $expectedExecutable) { throw 'Shortcut launched an unexpected application.' }
        if (-not $process.WaitForExit(120000)) { throw "$label timed out; inspect the installer and log." }
        if ($process.ExitCode -ne 0) { throw "$label failed with exit code $($process.ExitCode)." }
    } finally { $process.Dispose() }
}
function Make-Variant([string]$source, [string]$target) {
    $script = (Get-Content -LiteralPath $source -Raw).Replace('AppId={{F6AC8595-E870-5E91-9159-0B12C56E0E41}', 'AppId={{2B03D99A-765F-4A8F-A6B3-9A05CD6A0250}')
    if (-not $script.Contains('AppId={{2B03D99A-765F-4A8F-A6B3-9A05CD6A0250}')) { throw 'Failed to isolate the installer identity.' }
    $script = $script.Replace('Name: "{userprograms}\豆包 VR 字幕桥"', 'Name: "{userprograms}\豆包VR字幕验证"')
    $script = $script.Replace('Name: "{userdesktop}\豆包 VR 字幕桥"', 'Name: "{userdesktop}\豆包VR字幕验证"')
    $icon = Join-Path $rewriteRoot 'assets\bridge.ico'
    $language = Join-Path $repositoryRoot 'installer\ChineseSimplified.isl'
    $script = [regex]::Replace($script, '(?m)^SetupIconFile=.*$', { "SetupIconFile=$icon" })
    $script = [regex]::Replace($script, 'MessagesFile: "[^"\r\n]+"', { "MessagesFile: `"$language`"" })
    $script | Set-Content -LiteralPath $target -Encoding utf8
}
$expected = (Get-Content -LiteralPath ($archivePath + '.sha256') -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
if ((Get-FileHash -LiteralPath $archivePath).Hash -ne $expected) { throw 'Legacy fixture checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($entry in $archive.Entries) {
        $destination = [IO.Path]::GetFullPath((Join-Path $legacy $entry.FullName))
        if (-not $destination.StartsWith($legacy + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe legacy archive path.' }
    }
} finally { $archive.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $legacy)
$oldPayload = Join-Path $legacy 'DoubaoVR'
$oldScript = Join-Path $root 'old.iss'
$newScript = Join-Path $root 'new.iss'
Make-Variant (Join-Path $repositoryRoot 'installer\DoubaoVR.iss') $oldScript
Make-Variant (Join-Path $rewriteRoot 'installer\DoubaoVR.iss') $newScript
$oldHash = (Get-FileHash -LiteralPath (Join-Path $oldPayload 'DoubaoVR.exe')).Hash
& $compiler /Q '/DAppVersion=0.2.4' "/DPayloadDir=$oldPayload" "/DReleaseDir=$root" "/DApplicationSHA256=$oldHash" $oldScript
if ($LASTEXITCODE -ne 0) { throw 'Legacy isolated installer compilation failed.' }
& $compiler /Q "/DAppVersion=$version" "/DPayloadDir=$payload" "/DReleaseDir=$root" "/DVerificationFile=$verification" $newScript
if ($LASTEXITCODE -ne 0) { throw 'Native isolated installer compilation failed.' }
$oldInstaller = Join-Path $root 'DoubaoVR-v0.2.4-Setup-x64.exe'
$newInstaller = Join-Path $root "DoubaoVR-v$version-Setup-x64.exe"
$common = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
Run-Setup $oldInstaller ($common + @("/DIR=`"$install`"", "/LOG=`"$root\old-install.log`"")) 'Legacy clean install'
Assert-Profiles
if ((Get-Item -LiteralPath (Join-Path $install 'DoubaoVR.exe')).VersionInfo.ProductVersion.Trim() -ne '0.2.4') { throw 'Legacy install version mismatch.' }
$oldProcess = Start-Process -FilePath (Join-Path $install 'DoubaoVR.exe') -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 3
    Run-Setup $newInstaller ($common + @("/LOG=`"$root\upgrade.log`"")) 'Running legacy upgrade'
    if (-not $oldProcess.WaitForExit(15000)) { throw 'The old process remained running after upgrade.' }
} finally { $oldProcess.Dispose() }
Assert-Profiles
$remembered = Get-ItemProperty -LiteralPath $validationKey
if ($remembered.InstallLocation.TrimEnd('\') -ne $install -or $remembered.DisplayVersion -ne $version) { throw 'Upgrade did not preserve its installation directory/version.' }
foreach ($line in Get-Content -LiteralPath (Join-Path $install 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'Invalid installed manifest.' }
    if ((Get-FileHash -LiteralPath (Join-Path $install $Matches[2])).Hash -ne $Matches[1]) { throw 'Installed payload mismatch.' }
}
Run-Setup (Join-Path $install 'DoubaoVR.exe') @('--smoke-test') 'Installed application startup and teardown'
Assert-Profiles
Run-Setup $newInstaller ($common + @("/LOG=`"$root\reinstall.log`"")) 'Native reinstall'
Assert-Profiles
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '豆包VR字幕验证.lnk'
Run-Setup $shortcut @('--smoke-test') 'Installed desktop shortcut startup and teardown' (Join-Path $install 'DoubaoVR.exe')
Assert-Profiles
Run-Setup (Join-Path $install 'unins000.exe') ($common + @("/LOG=`"$root\uninstall.log`"")) 'Isolated uninstall'
Assert-Profiles
if (Test-Path -LiteralPath $validationKey) { throw 'Isolated uninstall registration remained.' }
$afterInstall = Get-ItemProperty -LiteralPath $productionKey -ErrorAction SilentlyContinue
if ($originalInstall.InstallLocation -ne $afterInstall.InstallLocation -or $originalInstall.DisplayVersion -ne $afterInstall.DisplayVersion) { throw 'The real installation registration was altered.' }
Write-Host 'PASS: isolated 0.2.4 install, running upgrade, Chinese/space path, remembered directory, complete payload, application startup, reinstall, shortcuts and uninstall with user settings preserved.'
Write-Host "Installer evidence: $root"
