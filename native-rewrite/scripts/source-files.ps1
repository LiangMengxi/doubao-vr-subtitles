function Get-PublicSourceFiles {
    $rewriteRoot = Split-Path $PSScriptRoot -Parent
    $repositoryRoot = Split-Path $rewriteRoot -Parent
    $nativeFiles = @('.gitignore','CMakeLists.txt','Directory.Build.props','Directory.Build.targets','NuGet.Config','README.md','THIRD_PARTY_NOTICES.md','build.ps1','dependencies.lock.json','global.json','package-release.ps1')
    $repositoryFiles = @('.gitattributes','.gitignore','LICENSE','README.md','THIRD_PARTY_NOTICES.md','VERSION','docs\RELEASE_NOTES.md','docs\RELEASING.md','docs\USAGE.md','docs\REFERENCES.md','docs\SOURCE_BUILD.md','scripts\build.ps1','scripts\package-release.ps1','scripts\start.cmd','licenses\README.md','licenses\InnoSetup-LICENSE.txt','licenses\OpenVR-LICENSE.txt')
    $files = @($nativeFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $rewriteRoot $_) })
    $files += $repositoryFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $repositoryRoot $_) }
    foreach ($directory in @('core','ui','assets','installer','scripts')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $rewriteRoot $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|tests|fixtures|backups|dependencies|out)\\' -and $_.Name -notin @('AGENTS.md','VALIDATION.md') -and $_.Extension -in @('.cpp','.h','.in','.cs','.xaml','.csproj','.json','.manifest','.ico','.png','.txt','.iss','.isl','.ps1') }
    }
    foreach ($file in @($files | Sort-Object FullName -Unique)) {
        if (-not $file.FullName.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe public source entry.' }
        $file
    }
}
