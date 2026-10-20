param([switch]$PrepareToolchain, [switch]$UiOnly, [switch]$Publish)
$ErrorActionPreference = 'Stop'
if ($UiOnly -and $Publish) { throw 'Publishing requires a complete native build and all automated checks.' }
$rewriteRoot = $PSScriptRoot
Push-Location -LiteralPath $rewriteRoot
$savedPath = $env:PATH
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
try {
    & (Join-Path $rewriteRoot 'scripts\check-policy.ps1')
    $buildTemp = Join-Path $rewriteRoot 'out\temp'
    New-Item -ItemType Directory -Path $buildTemp -Force | Out-Null
    $env:TEMP = $buildTemp
    $env:TMP = $buildTemp
    & (Join-Path $rewriteRoot 'scripts\fetch-dependencies.ps1') -Toolchain:$PrepareToolchain
    if (-not $UiOnly) {
        $localCompilerRoot = Join-Path $rewriteRoot 'dependencies\msvc\VC\Tools\MSVC'
        if (Test-Path -LiteralPath $localCompilerRoot) {
            $compilerRoot = (Get-ChildItem -LiteralPath $localCompilerRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
            $sdkRoot = Join-Path $rewriteRoot 'dependencies\sdk\c'
            $sdkInclude = (Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
            $sdkVersion = Split-Path $sdkInclude -Leaf
            $env:PATH = "$compilerRoot\bin\Hostx64\x64;$rewriteRoot\dependencies\cmake\bin;$sdkRoot\bin\$sdkVersion\x64;$savedPath"
            $env:INCLUDE = "$compilerRoot\include;$sdkInclude\ucrt;$sdkInclude\um;$sdkInclude\shared;$sdkInclude\winrt;$sdkInclude\cppwinrt"
            $env:LIB = "$compilerRoot\lib\x64;$sdkRoot\ucrt\x64;$sdkRoot\um\x64"
            cmake -S . -B out/core -G 'NMake Makefiles' -DCMAKE_BUILD_TYPE=Release "-DCMAKE_MT=$sdkRoot/bin/$sdkVersion/x64/mt.exe" "-DCMAKE_RC_COMPILER=$sdkRoot/bin/$sdkVersion/x64/rc.exe"
        } elseif (Get-Command cmake -ErrorAction SilentlyContinue) {
            cmake -S . -B out/core -A x64
        } else {
            throw 'C++ build tools were not found. Run .\build.ps1 -PrepareToolchain, or use a Visual Studio 2022 C++ developer shell with CMake.'
        }
        if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
        cmake --build out/core --config Release
        if ($LASTEXITCODE -ne 0) { throw 'Native core compilation failed.' }
    }
    dotnet restore ui/DoubaoVR.Desktop.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked WinUI dependency restore failed.' }
    dotnet build ui/DoubaoVR.Desktop.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    if (-not $UiOnly) {
        ctest --test-dir out/core -C Release --output-on-failure
        if ($LASTEXITCODE -ne 0) { throw 'GPU tests failed.' }
        dotnet run --project tests/ManagedChecks.csproj -c Release -- out/core/Release/DoubaoVR.Core.dll
        if ($LASTEXITCODE -ne 0) { throw 'Managed ABI and lifecycle tests failed.' }
        $motionCheck = Start-Process -FilePath (Join-Path $rewriteRoot 'ui\bin\Release\net8.0-windows10.0.19041.0\win-x64\DoubaoVR.exe') -ArgumentList '--settings-motion-test' -WindowStyle Hidden -PassThru
        try {
            if (-not $motionCheck.WaitForExit(30000)) { $motionCheck.Kill(); throw 'Settings motion checks timed out.' }
            if ($motionCheck.ExitCode -ne 0) { throw 'Settings motion checks failed. See the application log for per-frame layout results.' }
            Write-Host 'PASS: synchronized settings motion, interruption and resize checks.'
        } finally { $motionCheck.Dispose() }
    }
    if ($Publish) {
        & (Join-Path $rewriteRoot 'scripts\backup-source.ps1')
        $publishStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $stagingRoot = [IO.Path]::GetFullPath((Join-Path $rewriteRoot ('out\publish-' + $publishStamp)))
        $applicationRoot = [IO.Path]::GetFullPath((Join-Path $rewriteRoot 'out\app'))
        $previousRoot = [IO.Path]::GetFullPath((Join-Path $rewriteRoot ('out\app-backups\' + $publishStamp)))
        foreach ($targetPath in @($stagingRoot, $applicationRoot, $previousRoot)) {
            if (-not $targetPath.StartsWith($rewriteRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish path escaped the rewrite workspace.' }
        }
        dotnet publish ui/DoubaoVR.Desktop.csproj -c Release --no-restore -o $stagingRoot
        if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
        $publishedCore = Join-Path $stagingRoot 'DoubaoVR.Core.dll'
        if (-not (Test-Path -LiteralPath $publishedCore)) { throw 'Published app is missing the native core.' }
        foreach ($resource in @('App.xbf','DoubaoVR.pri')) {
            if (-not (Test-Path -LiteralPath (Join-Path $stagingRoot $resource))) { throw "Published app is missing $resource" }
        }
        if ((Get-FileHash -LiteralPath $publishedCore).Hash -ne (Get-FileHash -LiteralPath 'out/core/Release/DoubaoVR.Core.dll').Hash) { throw 'Published native core does not match the tested build.' }
        Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $stagingRoot 'README-NATIVE.md')
        Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination (Join-Path $stagingRoot 'THIRD_PARTY_NOTICES-NATIVE.md')
        $nugetCache = Join-Path $env:USERPROFILE '.nuget\packages'
        $licenseRoot = Join-Path $stagingRoot 'licenses'
        New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $nugetCache 'microsoft.windowsappsdk\1.7.250606001\license.txt') -Destination (Join-Path $licenseRoot 'WindowsAppSDK-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $nugetCache 'microsoft.windowsappsdk\1.7.250606001\NOTICE.txt') -Destination (Join-Path $licenseRoot 'WindowsAppSDK-NOTICE.txt')
        $runtimeRoot = (Get-ChildItem -LiteralPath (Join-Path $nugetCache 'microsoft.netcore.app.runtime.win-x64') -Directory | Where-Object Name -eq '8.0.30' | Select-Object -First 1).FullName
        if (-not $runtimeRoot) { throw 'Pinned .NET runtime license source was not found.' }
        Copy-Item -LiteralPath (Join-Path $runtimeRoot 'LICENSE.TXT') -Destination (Join-Path $licenseRoot 'DotNet-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $runtimeRoot 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseRoot 'DotNet-THIRD-PARTY-NOTICES.txt')
        Get-ChildItem -LiteralPath $stagingRoot -File -Recurse | Get-FileHash -Algorithm SHA256 | ForEach-Object {
            '{0}  {1}' -f $_.Hash, $_.Path.Substring($stagingRoot.Length + 1)
        } | Set-Content -LiteralPath (Join-Path $stagingRoot 'SHA256SUMS.txt') -Encoding utf8
        if (Test-Path -LiteralPath $applicationRoot) {
            New-Item -ItemType Directory -Path (Split-Path $previousRoot -Parent) -Force | Out-Null
            Move-Item -LiteralPath $applicationRoot -Destination $previousRoot
        }
        try { Move-Item -LiteralPath $stagingRoot -Destination $applicationRoot }
        catch {
            if ((Test-Path -LiteralPath $previousRoot) -and -not (Test-Path -LiteralPath $applicationRoot)) { Move-Item -LiteralPath $previousRoot -Destination $applicationRoot }
            throw
        }
        Write-Host "Published application: $rewriteRoot\out\app\DoubaoVR.exe"
    }
} finally {
    $env:PATH = $savedPath
    $env:INCLUDE = $savedInclude
    $env:LIB = $savedLib
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    Pop-Location
}
