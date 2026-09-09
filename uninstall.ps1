<#
    Removes Token Meter: the install folder, the shortcut, and the startup entry.
    Settings in %APPDATA%\TokenMeter are kept unless -Purge is passed.
#>
[CmdletBinding()]
param([switch]$Purge)

$ErrorActionPreference = 'Continue'
$target = Join-Path $env:LOCALAPPDATA 'Programs\TokenMeter'
$legacy = Join-Path $env:LOCALAPPDATA 'TokenMeter'          # binary lived here before, data still does
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Token Meter.lnk'
$settings = Join-Path $env:APPDATA 'TokenMeter'

Write-Host "`nRemoving Token Meter" -ForegroundColor Cyan

Get-Process TokenMeter -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'TokenMeter' -ErrorAction SilentlyContinue

try { Unregister-ScheduledTask -TaskName 'TokenMeter' -Confirm:$false -ErrorAction Stop } catch { }

if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force }   # cached UI and WebView2 profile

if ($Purge -and (Test-Path $settings)) {
    Remove-Item $settings -Recurse -Force
    Write-Host '  Settings removed.' -ForegroundColor DarkGray
} elseif (Test-Path $settings) {
    Write-Host "  Settings kept at $settings (use -Purge to delete)." -ForegroundColor DarkGray
}

Write-Host "Done.`n" -ForegroundColor Green
