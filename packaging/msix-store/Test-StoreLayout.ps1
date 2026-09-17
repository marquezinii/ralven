[CmdletBinding()]
param(
    [string]$LayoutRoot = "$PSScriptRoot/../../artifacts/msix-store/layout",
    [string]$PackageName = 'VemryxInc.Ralven',
    [string]$Publisher = 'CN=1FB9E268-F80A-40DF-8E57-BBB7C7849ABD',
    [string]$PublisherDisplayName = 'Vemryx Inc.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($LayoutRoot)
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'AppxManifest.xml') -Raw
if ($manifest.Package.Identity.Name -cne $PackageName -or
    $manifest.Package.Identity.Publisher -cne $Publisher -or
    $manifest.Package.Properties.PublisherDisplayName -cne $PublisherDisplayName) {
    throw 'Package identity does not match the expected Partner Center values.'
}
$caps = @($manifest.Package.Capabilities.ChildNodes | ForEach-Object { $_.GetAttribute('Name') } | Sort-Object)
if (($caps -join ',') -ne 'allowElevation,runFullTrust') { throw 'Unexpected capabilities.' }
if ($manifest.OuterXml -match '__|--demo|windows.startupTask') { throw 'Unresolved identity, demo or startup extension.' }
if ($manifest.Package.Applications.Application.GetAttribute('TrustLevel', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -ne 'mediumIL') {
    throw 'The desktop entry point must remain mediumIL.'
}
if ([Version]::Parse($manifest.Package.Identity.Version).Revision -ne 0) { throw 'Invalid Store revision.' }
if (-not (Test-Path -LiteralPath (Join-Path $root 'Ralven.Launcher.exe'))) { throw 'Launcher is missing.' }
$runtime = Join-Path $root 'Runtime'
$version = (Get-Content -LiteralPath (Join-Path $runtime 'active.json') -Raw | ConvertFrom-Json).Version
[Version]$parsedVersion = $null
if (-not [Version]::TryParse($version, [ref]$parsedVersion)) { throw 'Invalid bundled runtime version.' }
$app = Join-Path $runtime "versions/$version"
foreach ($name in 'Ralven.exe', 'coreclr.dll', 'PresentationFramework.dll', 'broker/Ralven.Broker.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $app $name))) { throw "Required payload missing: $name" }
}
$broker = [IO.Path]::GetFullPath((Join-Path $app 'broker'))
foreach ($line in Get-Content -LiteralPath (Join-Path $broker 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid Broker checksum line.' }
    $expected = $Matches[1]
    $target = [IO.Path]::GetFullPath((Join-Path $broker $Matches[2]))
    if (-not $target.StartsWith($broker + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Broker checksum path escaped the payload.'
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw 'Broker integrity mismatch.' }
}
Write-Host 'Store layout, minimal capabilities and Broker integrity passed.'
