param(
    [Parameter(Mandatory)][string]$Iscc,
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot 'releases')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'native-rewrite\package-release.ps1') -Iscc $Iscc -ReleaseDirectory $ReleaseDirectory
