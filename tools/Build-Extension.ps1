<#
.SYNOPSIS
    Builds the browser extension for Chromium (Chrome, Edge, Brave, Opera, Vivaldi) and Firefox.

.DESCRIPTION
    Output under publish\extension:
      chromium\                                   unpacked, keeps the manifest "key" (fixed dev ID) for Load unpacked
      firefox\                                    unpacked, for about:debugging > Load Temporary Add-on
      download-solutions-chromium-<ver>.zip       store upload; "key" removed (the Chrome Web Store rejects it)
      download-solutions-firefox-<ver>.zip        AMO upload

.PARAMETER IconDir
    Folder with icon-16.png, icon-32.png, icon-48.png, icon-128.png. Placeholder icons are generated when omitted.

.EXAMPLE
    powershell -NoProfile -File tools\Build-Extension.ps1 -IconDir "C:\path\to\icons"
#>
[CmdletBinding()]
param(
    [string]$IconDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'extension\src'
$outRoot = Join-Path $root 'publish\extension'
$sizes = 16, 32, 48, 128

function New-PlaceholderIcon([string]$Path, [int]$Size) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size
    try {
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.Clear([System.Drawing.Color]::Transparent)
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 58, 123, 213))
            $g.FillRectangle($brush, 0, 0, $Size, $Size)
            $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, $Size / 10))
            $mid = $Size / 2
            $g.DrawLine($pen, $mid, $Size * 0.2, $mid, $Size * 0.65)
            $g.DrawLine($pen, $Size * 0.3, $Size * 0.45, $mid, $Size * 0.68)
            $g.DrawLine($pen, $Size * 0.7, $Size * 0.45, $mid, $Size * 0.68)
            $g.DrawLine($pen, $Size * 0.25, $Size * 0.82, $Size * 0.75, $Size * 0.82)
            $pen.Dispose()
            $brush.Dispose()
        }
        finally { $g.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Copy-Icons([string]$Target) {
    $iconOut = Join-Path $Target 'icons'
    New-Item -ItemType Directory -Force -Path $iconOut | Out-Null
    foreach ($size in $sizes) {
        $name = "icon-$size.png"
        $dest = Join-Path $iconOut $name
        if ($IconDir) {
            $src = Join-Path $IconDir $name
            if (-not (Test-Path -LiteralPath $src)) { throw "Missing icon: $src" }
            Copy-Item -LiteralPath $src -Destination $dest -Force
        }
        else {
            New-PlaceholderIcon -Path $dest -Size $size
        }
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false))
}

function New-Zip([string]$Folder, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Resolve-Path -LiteralPath $Folder).Path.TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem -LiteralPath $Folder -Recurse -File) {
            # Forward slashes: AMO rejects backslash entry names.
            $entry = $file.FullName.Substring($base.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

function Build-Target([string]$Name, [string]$ManifestFile, [bool]$StripKeyForZip) {
    $target = Join-Path $outRoot $Name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
    Copy-Icons -Target $target

    $manifestText = Get-Content -LiteralPath (Join-Path $root "extension\$ManifestFile") -Raw -Encoding UTF8
    $manifest = $manifestText | ConvertFrom-Json
    Write-Utf8NoBom -Path (Join-Path $target 'manifest.json') -Text $manifestText

    $zipPath = Join-Path $outRoot ("download-solutions-{0}-{1}.zip" -f $Name, $manifest.version)
    if ($StripKeyForZip) {
        $staging = Join-Path $outRoot "$Name-store"
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        Copy-Item -LiteralPath $target -Destination $staging -Recurse
        $manifest.PSObject.Properties.Remove('key')
        Write-Utf8NoBom -Path (Join-Path $staging 'manifest.json') -Text ($manifest | ConvertTo-Json -Depth 10)
        New-Zip -Folder $staging -ZipPath $zipPath
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    else {
        New-Zip -Folder $target -ZipPath $zipPath
    }

    Write-Host "Built $Name -> $target and $zipPath"
}

try {
    New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
    if (-not $IconDir) { Write-Warning 'No -IconDir given; using generated placeholder icons.' }
    Build-Target -Name 'chromium' -ManifestFile 'manifest.chromium.json' -StripKeyForZip $true
    Build-Target -Name 'firefox' -ManifestFile 'manifest.firefox.json' -StripKeyForZip $false
}
catch {
    Write-Error "Extension build failed: $($_.Exception.Message)"
    exit 1
}
