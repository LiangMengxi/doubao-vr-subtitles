param([switch]$PrepareToolchain, [switch]$UiOnly, [switch]$Publish)
$ErrorActionPreference = 'Stop'
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'native-rewrite\build.ps1') -PrepareToolchain:$PrepareToolchain -UiOnly:$UiOnly -Publish:$Publish
