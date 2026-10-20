param([switch]$PrepareToolchain, [switch]$UiOnly, [switch]$Publish)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'native-rewrite\build.ps1') -PrepareToolchain:$PrepareToolchain -UiOnly:$UiOnly -Publish:$Publish
