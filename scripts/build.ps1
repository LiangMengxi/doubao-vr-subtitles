param([switch]$PrepareToolchain, [switch]$UiOnly, [switch]$Publish, [switch]$SourceBuild)
$ErrorActionPreference = 'Stop'
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'native-rewrite\build.ps1') -PrepareToolchain:$PrepareToolchain -UiOnly:$UiOnly -Publish:$Publish -SourceBuild:$SourceBuild
