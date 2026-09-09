<#
    Publishes Token Meter to %LOCALAPPDATA%\Programs\TokenMeter, adds a Start Menu shortcut,
    registers it to start with Windows, and launches it. Re-run to update in place.

    The binary goes under Programs\ rather than straight into %LOCALAPPDATA%, which is where
    per-user apps belong and, more practically, is a location Windows will actually launch from at
    logon: an executable sitting directly in the AppData root is the classic malware persistence
    shape, and launching one from a scheduled task or Run key gets blocked on some machines with
    no error surfaced anywhere. Data stays in %LOCALAPPDATA%\TokenMeter, which is never executed.
#>
[CmdletBinding()]
param(
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'src\TokenMeter'
$target = Join-Path $env:LOCALAPPDATA 'Programs\TokenMeter'
$dataDir = Join-Path $env:LOCALAPPDATA 'TokenMeter'
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

# The unpacked UI is rewritten on every launch; clearing it avoids serving a previous build.
$webCache = Join-Path $dataDir 'web'
if (Test-Path $webCache) { Remove-Item $webCache -Recurse -Force -ErrorAction SilentlyContinue }

# Earlier versions installed the binary into the data folder. Leave the data, drop the binary.
$legacyExe = Join-Path $dataDir 'TokenMeter.exe'
if (Test-Path $legacyExe) {
    Step 'Removing the binary from the old location'
    Remove-Item $legacyExe -Force -ErrorAction SilentlyContinue
    Get-ChildItem $dataDir -Filter '*.xml' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

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
    # A logon task rather than the Run key: the Run key is skipped when Fast Startup resumes a
    # session instead of logging on afresh, and it races the shell. Neither needs elevation.
    Step 'Enabling start with Windows'
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'TokenMeter' -ErrorAction SilentlyContinue

    $ok = $false
    try {
        $action = New-ScheduledTaskAction -Execute $exe -Argument '--autostart' -WorkingDirectory $target
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
        $trigger.Delay = 'PT10S'
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
        Register-ScheduledTask -TaskName 'TokenMeter' -Action $action -Trigger $trigger `
            -Settings $settings -Description 'Starts Token Meter when you sign in.' -Force -ErrorAction Stop | Out-Null
        $ok = $true
    } catch {
        Step "  scheduled task refused ($($_.Exception.Message.Trim())), using the Run key instead"
    }

    if (-not $ok) {
        New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
            -Name 'TokenMeter' -Value "`"$exe`" --autostart" -PropertyType String -Force | Out-Null
    }
}

if (-not $NoLaunch) {
    Step 'Starting'
    Start-Process $exe
}

Write-Host "`nInstalled. Look for the coin in your system tray." -ForegroundColor Green
Write-Host "Left-click for the panel, double-click for the dashboard.`n" -ForegroundColor DarkGray
