$ErrorActionPreference = 'Stop'

# Installs the .NET SDK into the user profile (no admin rights required).
#
# This downloads the official SDK zip and extracts it directly instead of using
# dotnet-install.ps1: on some machines that bootstrap script hangs for minutes in
# PowerShell 5.1 without ever starting a download, while the direct URL responds
# in milliseconds.

$version = '8.0.404'
$url = "https://dotnetcli.azureedge.net/dotnet/Sdk/$version/dotnet-sdk-$version-win-x64.zip"
$expected = 281358929

$downloadDir = Join-Path $env:LOCALAPPDATA 'dotnet-sdk-download'
$zip = Join-Path $downloadDir "dotnet-sdk-$version-win-x64.zip"
$installDir = Join-Path $env:LOCALAPPDATA 'dotnet-sdk'

New-Item -ItemType Directory -Force -Path $downloadDir, $installDir | Out-Null

$needsDownload = $true
if (Test-Path $zip) {
    $length = (Get-Item $zip).Length
    Write-Host "existing zip: $length bytes (expected $expected)"
    if ($length -eq $expected) { $needsDownload = $false }
}

if ($needsDownload) {
    Write-Host "downloading $url"
    try {
        Import-Module BitsTransfer -ErrorAction Stop
        Start-BitsTransfer -Source $url -Destination $zip -Priority Foreground -Description "dotnet sdk $version"
    }
    catch {
        Write-Host "BITS unavailable ($($_.Exception.Message)); falling back to WebClient"
        $client = New-Object System.Net.WebClient
        $client.DownloadFile($url, $zip)
    }
}

$size = (Get-Item $zip).Length
Write-Host "zip size: $size bytes"
if ($size -ne $expected) { throw "Zip size mismatch: got $size, expected $expected" }

Write-Host 'extracting...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $installDir)

Write-Host '--- installed SDKs ---'
& (Join-Path $installDir 'dotnet.exe') --list-sdks

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Write-Host 'done'
