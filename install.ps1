# Installs Lyrenne VR for the current user:
#   1. copies the app to %LOCALAPPDATA%\Programs\LyrenneVR
#   2. registers the OpenXR layer (one admin prompt, OpenXR reads layers from HKLM)
#   3. starts it with Windows and adds a Start menu shortcut
#   4. launches it
# Run from the packaged folder: powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LyrenneVR'
$layerJson = Join-Path $dest 'layer\XR_APILAYER_LYRENNE_wrist_player.json'
$layerKey = 'HKLM:\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit'

if (-not (Test-Path (Join-Path $src 'LyrenneVR.exe'))) { throw "Run this from the packaged LyrenneVR folder (dist\LyrenneVR)." }

Get-Process LyrenneVR -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
if ((Resolve-Path $src).Path -ne $dest) {
    New-Item -ItemType Directory $dest -Force | Out-Null
    Copy-Item (Join-Path $src '*') $dest -Recurse -Force
}

$registered = $false
try {
    $registered = (Get-ItemProperty $layerKey -ErrorAction Stop).PSObject.Properties.Name -contains $layerJson
} catch { }
if (-not $registered) {
    Write-Host 'Registering the OpenXR layer (Windows will ask for admin rights once)...'
    $cmd = "New-Item -Path '$layerKey' -Force | Out-Null; New-ItemProperty -Path '$layerKey' -Name '$layerJson' -PropertyType DWord -Value 0 -Force | Out-Null"
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $cmd
    if ($p.ExitCode -ne 0) { Write-Warning 'Layer registration was cancelled. SteamVR games will still work, OpenXR games on VDXR will not.' }
}

Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LyrenneVR' -Value "`"$dest\LyrenneVR.exe`""

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Lyrenne VR.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = Join-Path $dest 'LyrenneVR.exe'
$s.WorkingDirectory = $dest
$s.Description = 'Lyrenne wrist player for VR'
$s.Save()

Start-Process (Join-Path $dest 'LyrenneVR.exe')
Write-Host "Installed to $dest. Lyrenne VR is running in the tray."
