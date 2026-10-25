# Install Arno's Pet Tracker for the current user (no administrator rights needed):
# copies the program to %LOCALAPPDATA%\Programs\arno-pet-tracker, adds a Start menu
# shortcut and, unless -NoAutostart is given, starts it with Windows.
# Run from the unzipped folder:  powershell -ExecutionPolicy Bypass -File .\install.ps1
param([switch]$NoAutostart)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\arno-pet-tracker'
if (-not (Test-Path (Join-Path $source 'arno-pet-tracker.exe'))) { throw "Run this from the unzipped arno-pet-tracker folder." }
Get-Process arno-pet-tracker, arnoa2-capture -ErrorAction SilentlyContinue | Stop-Process -Force
if ((Resolve-Path $source).Path -ne $target) {
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item -Recurse -Force (Join-Path $source '*') $target
}
$shell = New-Object -ComObject WScript.Shell
function Shortcut($path) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = Join-Path $target 'arno-pet-tracker.exe'
    $link.WorkingDirectory = $target
    $link.Description = "Arno's Pet Tracker"
    $link.Save()
}
Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) "Arno's Pet Tracker.lnk")
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) "Arno's Pet Tracker.lnk"
if ($NoAutostart) { Remove-Item $startup -ErrorAction SilentlyContinue } else { Shortcut $startup }
$npcap = Test-Path (Join-Path $env:WINDIR 'System32\Npcap\wpcap.dll')
Write-Host "Installed to $target"
if (-not $npcap) { Write-Host "Npcap is not installed. Install it from https://npcap.com/#download (default options) before starting the tracker." -ForegroundColor Yellow }
