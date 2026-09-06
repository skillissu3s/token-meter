<#
    Removes Token Meter: the install folder, the shortcut, and the startup entry.
    Settings in %APPDATA%\TokenMeter are kept unless -Purge is passed.
#>
[CmdletBinding()]
param([switch]$Purge)

$ErrorActionPreference = 'Continue'
$target = Join-Path $env:LOCALAPPDATA 'TokenMeter'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Token Meter.lnk'
$settings = Join-Path $env:APPDATA 'TokenMeter'

Write-Host "`nRemoving Token Meter" -ForegroundColor Cyan

Get-Process TokenMeter -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'TokenMeter' -ErrorAction SilentlyContinue

if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
if (Test-Path $target) { Remove-Item $target -Recurse -Force }

if ($Purge -and (Test-Path $settings)) {
    Remove-Item $settings -Recurse -Force
    Write-Host '  Settings removed.' -ForegroundColor DarkGray
} elseif (Test-Path $settings) {
    Write-Host "  Settings kept at $settings (use -Purge to delete)." -ForegroundColor DarkGray
}

Write-Host "Done.`n" -ForegroundColor Green
