param(
    [Parameter(Mandatory)] [string] $AssetPath,
    [Parameter(Mandatory)] [string] $ExpectedSourceRevision
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$configuration = Get-ReleaseConfiguration (Split-Path $PSScriptRoot -Parent)
Assert-ReleaseAssetSet $AssetPath $configuration $ExpectedSourceRevision
Write-Output 'Exact unsigned preview asset set, SHA-256 checksums and expected source identity verified.'
