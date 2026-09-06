<#
    Publishes Token Meter to %LOCALAPPDATA%\TokenMeter, adds a Start Menu shortcut,
    registers it to start with Windows, and launches it. Re-run to update in place.
#>
[CmdletBinding()]
param(
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'src\TokenMeter'
$target = Join-Path $env:LOCALAPPDATA 'TokenMeter'
$exe = Join-Path $target 'TokenMeter.exe'

function Step($text) { Write-Host "  $text" -ForegroundColor DarkGray }

Write-Host "`nToken Meter" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required to build Token Meter. Install .NET 10 from https://dotnet.microsoft.com/download'
}

# A running instance would hold the exe open.
Get-Process TokenMeter -ErrorAction SilentlyContinue | ForEach-Object {
    Step 'Closing the running instance'
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

Step 'Building'
$publish = Join-Path $repo 'artifacts\publish'
dotnet publish $project -c Release -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Step "Installing to $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $target -Recurse -Force

# Stale unpacked web assets from an older version are keyed by version and harmless, but tidy them.
$webCache = Join-Path $env:LOCALAPPDATA 'TokenMeter\web'
if (Test-Path $webCache) { Remove-Item $webCache -Recurse -Force -ErrorAction SilentlyContinue }

Step 'Adding Start Menu shortcut'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $startMenu 'Token Meter.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = $exe
$link.Description = 'Token usage across your AI coding tools'
$link.Save()

if (-not $NoStartup) {
    Step 'Enabling start with Windows'
    New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'TokenMeter' -Value "`"$exe`"" -PropertyType String -Force | Out-Null
}

if (-not $NoLaunch) {
    Step 'Starting'
    Start-Process $exe
}

Write-Host "`nInstalled. Look for the coin in your system tray." -ForegroundColor Green
Write-Host "Left-click for the panel, double-click for the dashboard.`n" -ForegroundColor DarkGray
