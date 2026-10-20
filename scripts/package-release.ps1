param(
    [Parameter(Mandatory)][string]$Iscc,
    [string]$ReleaseDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'releases')
)
$ErrorActionPreference = 'Stop'
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'native-rewrite\package-release.ps1') -Iscc $Iscc -ReleaseDirectory $ReleaseDirectory
