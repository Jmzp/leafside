<#
.SYNOPSIS
    Builds the self-contained app for x64 and ARM64 and zips each one into artifacts\.
.EXAMPLE
    .\build\publish.ps1                 # version from Directory.Build.props
    .\build\publish.ps1 -Version 1.2.0
#>
param(
    [string]$Version,
    [string[]]$Platforms = @("x64", "ARM64")
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Select-Object -First 1
}
New-Item -ItemType Directory -Force $artifacts | Out-Null

foreach ($platform in $Platforms) {
    $rid = if ($platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
    $folder = Join-Path $artifacts "PdfReader-$rid"
    if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }

    dotnet publish (Join-Path $root "src\PdfReader.App") -c Release -p:Platform=$platform -p:Version=$Version -o $folder -nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $platform" }

    foreach ($required in "PdfReader.exe", "pdfium.dll", "PdfReader.pri", "MainWindow.xbf", "README.txt") {
        if (-not (Test-Path (Join-Path $folder $required))) { throw "$required missing from the $platform build" }
    }

    $zip = Join-Path $artifacts "PdfReader-$Version-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $folder -DestinationPath $zip
    Write-Host "Created $zip"
}
