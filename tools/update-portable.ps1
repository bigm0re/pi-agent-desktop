<#
.SYNOPSIS
    Applies a new portable build over the folder that is currently running.

.DESCRIPTION
    Pi Agent Desktop is distributed as a portable zip: no installer, no install
    directory, no registry footprint. Upgrading therefore just means replacing the
    files in place, which this script does safely - it stops the running app,
    backs up the current folder, copies the new files over, and restarts.

    Run it from a normal PowerShell window, not from inside the app.

    NOTE: the app hosts the pi agent server as a child process in a Win32 job
    object, so stopping it also ends any agent session it is hosting.

.PARAMETER Zip
    A downloaded release zip, e.g.
    PiAgentDesktop-0.1.1-win-x64-portable.zip

.PARAMETER From
    Alternatively, a folder that already holds the new build (e.g. .\dist).

.PARAMETER TargetDir
    Folder to update. Defaults to the folder of the running PiAgentDesktop.exe.

.PARAMETER NoRestart
    Do not start the app again afterwards.

.EXAMPLE
    .\tools\update-portable.ps1 -Zip "$env:USERPROFILE\Downloads\PiAgentDesktop-0.1.1-win-x64-portable.zip"

.EXAMPLE
    .\tools\update-portable.ps1 -From .\dist -TargetDir D:\PiAgentDesktop -NoRestart
#>
[CmdletBinding(DefaultParameterSetName = 'Zip')]
param(
    [Parameter(ParameterSetName = 'Zip', Mandatory = $true)]
    [string]$Zip,

    [Parameter(ParameterSetName = 'From', Mandatory = $true)]
    [string]$From,

    [string]$TargetDir,

    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

function Get-TargetFolder {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "Target folder not found: $Explicit" }
        return (Resolve-Path $Explicit).Path
    }

    $running = Get-Process PiAgentDesktop -ErrorAction SilentlyContinue |
        Where-Object { $_.Path } |
        Select-Object -First 1

    if ($running) {
        return (Split-Path $running.Path -Parent)
    }

    throw 'Nothing is running; pass -TargetDir <folder> explicitly.'
}

# --- 1. stage the new build in a temp folder -------------------------------
$staging = Join-Path $env:TEMP ('PiAgentDesktop-staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

if ($PSCmdlet.ParameterSetName -eq 'Zip') {
    if (-not (Test-Path $Zip)) { throw "Zip not found: $Zip" }
    Write-Host "source    : $Zip" -ForegroundColor Cyan
    Expand-Archive -Path $Zip -DestinationPath $staging -Force
}
else {
    if (-not (Test-Path $From)) { throw "Folder not found: $From" }
    Write-Host "source    : $From" -ForegroundColor Cyan
    Copy-Item (Join-Path $From '*') $staging -Recurse -Force
}

$stagedExe = Join-Path $staging 'PiAgentDesktop.exe'
if (-not (Test-Path $stagedExe)) {
    throw "The staged build has no PiAgentDesktop.exe (looked in $staging)."
}

$newVersion = (Get-Item $stagedExe).VersionInfo.ProductVersion
$target = Get-TargetFolder -Explicit $TargetDir
Write-Host "target    : $target" -ForegroundColor Cyan
Write-Host "version   : $newVersion" -ForegroundColor Cyan

$targetExe = Join-Path $target 'PiAgentDesktop.exe'
if (Test-Path $targetExe) {
    $oldVersion = (Get-Item $targetExe).VersionInfo.ProductVersion
    Write-Host "current   : $oldVersion" -ForegroundColor DarkGray
    if ($oldVersion -eq $newVersion) {
        Write-Host 'note      : same version - reinstalling the same build' -ForegroundColor Yellow
    }
}

# --- 2. stop the running app ----------------------------------------------
$running = @(Get-Process PiAgentDesktop -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "stopping  : $($running.Count) instance(s); hosted agent sessions end" -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 3
}

# --- 3. back up, then copy the new files over -----------------------------
if (Test-Path $target) {
    $backup = "$target.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item $target $backup -Recurse -Force
    Write-Host "backup    : $backup" -ForegroundColor DarkGray
}
else {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
}

Copy-Item (Join-Path $staging '*') $target -Recurse -Force
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$installed = (Get-Item $targetExe).VersionInfo.ProductVersion
Write-Host "applied   : $target (now $installed)" -ForegroundColor Green

# --- 4. restart ------------------------------------------------------------
if (-not $NoRestart) {
    Start-Process -FilePath $targetExe
    Write-Host 'started   : check the window title or the tray menu for the version' -ForegroundColor Green
}
