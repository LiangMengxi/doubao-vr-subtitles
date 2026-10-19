param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path $PSScriptRoot -Parent

function Get-NativePolicyErrors([string]$source, [bool]$platformBoundary) {
    $source = [regex]::Replace($source, '(?s)R"\(.*?\)"', '""')
    $source = [regex]::Replace($source, '"(?:\\.|[^"\\])*"', '""')
    $source = [regex]::Replace($source, '(?m)//.*$', '')
    $source = [regex]::Replace($source, '(?s)/\*.*?\*/', '')
    if ($source -cmatch '\bnew\s+[A-Za-z_(:]|\bdelete\s+[^;]|\b(malloc|calloc|realloc|free)\s*\(') { 'manual memory allocation' }
    if ($source -cmatch '(->|\.)Release\s*\(') { 'manual COM release' }
    if (-not $platformBoundary) {
        if ($source -cmatch '(?<![.\w])(?:void|char|wchar_t|u?int(?:8|16|32|64)_t|[A-Z][A-Za-z_0-9:]*)(?:\s+const)?\s*\*') { 'raw pointer declaration or cast' }
        if ($source -cmatch '\bauto\s+[A-Za-z_]\w*\s*=\s*[\w.:>\-]+\.get\s*\(\s*\)\s*;|\bauto\s+[A-Za-z_]\w*\s*=\s*&') { 'borrowed address stored in core logic' }
        if ($source -cmatch '->(?:Map|Unmap)\s*\(') { 'mapping outside the RAII boundary' }
    }
}

if ($SelfTest) {
    $rejected = @('void* state;', 'ID3D11Texture2D* image;', 'auto texture = owner.get();', 'auto address = &state;', 'auto state = new Bridge();', 'delete state;', 'free(buffer);', 'context->Map(resource);')
    foreach ($source in $rejected) {
        if (@(Get-NativePolicyErrors $source $false).Count -eq 0) { throw "Policy failed to reject: $source" }
    }
    $accepted = 'std::unique_ptr<Bridge> state; winrt::com_ptr<ID3D11Texture2D> image; Owner(const Owner&) = delete;'
    if (@(Get-NativePolicyErrors $accepted $false).Count -ne 0) { throw 'Policy rejected RAII code.' }
    if (@(Get-NativePolicyErrors 'ID3D11ShaderResourceView* borrowed;' $true).Count -ne 0) { throw 'SDK boundary was rejected.' }
    Write-Host 'PASS: policy rejects raw ownership, borrowed core addresses and unscoped mappings.'
}

$failures = @()
$nativeFiles = Get-ChildItem -LiteralPath (Join-Path $rewriteRoot 'core'),(Join-Path $rewriteRoot 'tests') -Recurse -File | Where-Object { $_.Extension -in '.cpp','.h' -and $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($file in $nativeFiles) {
    $relative = $file.FullName.Substring($rewriteRoot.Length + 1)
    foreach ($issue in @(Get-NativePolicyErrors (Get-Content -LiteralPath $file.FullName -Raw) ($relative -eq 'core\platform.cpp'))) {
        $failures += $relative + ': ' + $issue
    }
}
$managedFiles = Get-ChildItem -LiteralPath (Join-Path $rewriteRoot 'ui'),(Join-Path $rewriteRoot 'tests') -Recurse -File -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($file in $managedFiles) {
    $source = Get-Content -LiteralPath $file.FullName -Raw
    if ($source -match '\bunsafe\b|DangerousGetHandle|DangerousAddRef|AcquirePointer') { $failures += $file.Name + ': unsafe managed memory access' }
    if ($file.Name -ne 'NativeBridge.cs' -and $source -match 'Marshal\.(?:Alloc|Free)|NativeMemory\.') { $failures += $file.Name + ': unmanaged allocation outside SafeBuffer wrapper' }
}
$cmake = Get-Content -LiteralPath (Join-Path $rewriteRoot 'CMakeLists.txt') -Raw
if ($cmake -match 'FetchContent|ExternalProject|vcpkg|conan|CPMAddPackage') { $failures += 'CMake must consume the local verified SDK directly.' }
foreach ($project in Get-ChildItem -LiteralPath (Join-Path $rewriteRoot 'ui'),(Join-Path $rewriteRoot 'tests') -Filter '*.csproj') {
    [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in $xml.SelectNodes('//PackageReference')) {
        if ($reference.Version -notmatch '^\[\d+\.\d+\.[\d.]+\]$') { $failures += $project.Name + ': package version must be exact: ' + $reference.Include }
    }
}
if ($failures.Count -gt 0) { throw ('Build policy failed:' + [Environment]::NewLine + ($failures -join [Environment]::NewLine)) }
Write-Host 'Build policy passed.'
