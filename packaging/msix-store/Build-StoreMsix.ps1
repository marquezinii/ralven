[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$PackageVersion = '1.7.1.0',

    [ValidateNotNullOrEmpty()][string]$PackageName = 'VemryxInc.Ralven',
    [ValidateNotNullOrEmpty()][string]$Publisher = 'CN=1FB9E268-F80A-40DF-8E57-BBB7C7849ABD',
    [ValidateNotNullOrEmpty()][string]$PublisherDisplayName = 'Vemryx Inc.',
    [switch]$Harden,
    [string]$CertificateThumbprint,

    [switch]$SkipPortableBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Version]::Parse($PackageVersion).Revision -ne 0) { throw 'Store package revision must be zero.' }
if ($PackageName.Contains('__') -or $Publisher.Contains('__') -or $PublisherDisplayName.Contains('__')) {
    throw 'Replace identity placeholders with the exact Partner Center values.'
}

function New-SquarePng([string]$Source, [string]$Destination, [int]$Size) {
    Add-Type -AssemblyName System.Drawing
    $sourceImage = [System.Drawing.Image]::FromFile($Source)
    $targetImage = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($targetImage)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($sourceImage, 0, 0, $Size, $Size)
        $targetImage.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $targetImage.Dispose()
        $sourceImage.Dispose()
    }
}

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = Join-Path $workspace 'artifacts'
$portableRoot = Join-Path $artifactsRoot 'Ralven-win-x64'
$pocRoot = Join-Path $artifactsRoot 'msix-store'
$layoutRoot = Join-Path $pocRoot 'layout'
$packagePath = Join-Path $pocRoot "Ralven-Store-$PackageVersion-x64.msix"
$manifestTemplate = Join-Path $PSScriptRoot 'AppxManifest.xml'
$makeAppx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe |
    Where-Object FullName -Like '*\x64\makeappx.exe' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe |
    Where-Object FullName -Like '*\x64\signtool.exe' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $makeAppx) { throw 'makeappx.exe was not found in the Windows SDK.' }
if ($CertificateThumbprint -and -not $signTool) { throw 'signtool.exe was not found in the Windows SDK.' }

Push-Location $workspace
try {
    if (-not $SkipPortableBuild) {
        & .\scripts\Build-Portable.ps1 -Runtime win-x64 -Configuration Release -Harden:$Harden -SkipArchives
        if ($LASTEXITCODE -ne 0) { throw 'The portable payload build failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $portableRoot 'Ralven.Launcher.exe'))) {
        throw "Portable payload not found at '$portableRoot'."
    }

    if (Test-Path -LiteralPath $pocRoot) {
        if (-not ([IO.Path]::GetFullPath($pocRoot).StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw 'Unsafe artifacts path.' }
        Remove-Item -LiteralPath $pocRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $layoutRoot | Out-Null
    Copy-Item -Path (Join-Path $portableRoot '*') -Destination $layoutRoot -Recurse
    New-Item -ItemType Directory -Path (Join-Path $layoutRoot 'Assets') | Out-Null
    $sourceLogo = (Resolve-Path .\src\Ralven.App\Assets\Ralven.png).Path
    New-SquarePng $sourceLogo (Join-Path $layoutRoot 'Assets\StoreLogo.png') 50
    New-SquarePng $sourceLogo (Join-Path $layoutRoot 'Assets\Square44x44Logo.png') 44
    New-SquarePng $sourceLogo (Join-Path $layoutRoot 'Assets\Square150x150Logo.png') 150

    [xml]$manifest = Get-Content -LiteralPath $manifestTemplate -Raw
    $manifest.Package.Identity.SetAttribute('Name', $PackageName)
    $manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
    $manifest.Package.Identity.SetAttribute('Version', $PackageVersion)
    $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $manifest.Save((Join-Path $layoutRoot 'AppxManifest.xml'))

    & $makeAppx pack /d $layoutRoot /p $packagePath /o
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed to create the MSIX package.' }

    if ($CertificateThumbprint) {
        & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /s My $packagePath
        if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the MSIX package.' }
        & $signTool verify /pa /v $packagePath
        if ($LASTEXITCODE -ne 0) { throw 'SignTool could not verify the MSIX signature.' }
    }

    Write-Host "MSIX certification candidate ready: $packagePath" -ForegroundColor Green
}
finally {
    Pop-Location
}

