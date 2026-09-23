<#
.SYNOPSIS
    Builds (and optionally runs) the native Pi Agent Desktop shell.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Run
    .\build.ps1 -SmokeTest
    .\build.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Bundles the .NET runtime into the output (larger, no runtime prerequisite).
    [switch]$SelfContained,

    # Launches the built app after a successful build.
    [switch]$Run,

    # Runs the built app with --smoke-test and prints the result.
    [switch]$SmokeTest,

    # Copies the build to the installed location (D:\PiAgentDesktop by default).
    [string]$DeployTo
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\PiAgentDesktop\PiAgentDesktop.csproj'
$output = Join-Path $root 'dist'

function Resolve-DotNet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'dotnet-sdk\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks) { return $candidate }
    }

    throw "No .NET SDK found. Install one with:`n  winget install Microsoft.DotNet.SDK.8`nor:`n  powershell -File tools\install-dotnet-sdk.ps1"
}

$dotnet = Resolve-DotNet
Write-Host "dotnet : $dotnet" -ForegroundColor DarkGray
& $dotnet --list-sdks | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

Write-Host 'icons  : regenerating assets' -ForegroundColor DarkGray
node (Join-Path $root 'tools\make-icons.js')

Write-Host "build  : $Configuration (self-contained=$($SelfContained.IsPresent))" -ForegroundColor Cyan
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-o', $output,
    '-p:PublishSingleFile=true',
    "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())"
)

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$exe = Join-Path $output 'PiAgentDesktop.exe'
Write-Host "output : $exe" -ForegroundColor Green

if ($SmokeTest) {
    Write-Host 'smoke  : running --smoke-test' -ForegroundColor Cyan
    Push-Location $root
    try {
        $stdout = Join-Path $env:TEMP 'pi-agent-desktop-smoke.out'
        $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WorkingDirectory $root `
            -PassThru -RedirectStandardOutput $stdout

        if (-not $process.WaitForExit(180000)) {
            & taskkill /PID $process.Id /T /F 2>&1 | Out-Null
            Write-Warning 'Smoke test timed out after 180s and was killed.'
        }

        $result = Join-Path $root 'smoke-result.json'
        if (Test-Path $result) {
            $json = Get-Content $result -Raw | ConvertFrom-Json
            $colour = if ($json.ok) { 'Green' } else { 'Red' }
            Write-Host ("smoke  : ok={0} state={1} port={2} webview2={3} navigation={4}" -f `
                $json.ok, $json.serverState, $json.port, $json.webView2Version, $json.navigationSucceeded) -ForegroundColor $colour
            Write-Host ("         npmCommand={0} probe={1}" -f $json.npmCommand, $json.npmProbe) -ForegroundColor DarkGray
            Write-Host "         result: $result" -ForegroundColor DarkGray
        }
        else {
            Write-Warning 'smoke-result.json was not produced.'
        }
    }
    finally {
        Pop-Location
    }
}

if ($DeployTo) {
    if (-not (Test-Path $DeployTo)) { New-Item -ItemType Directory -Force -Path $DeployTo | Out-Null }
    Copy-Item (Join-Path $output '*') $DeployTo -Recurse -Force
    Write-Host "deploy : copied to $DeployTo" -ForegroundColor Green
}

if ($Run) {
    Write-Host 'run    : starting app' -ForegroundColor Cyan
    Start-Process -FilePath $exe
}
