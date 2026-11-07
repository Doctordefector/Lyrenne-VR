# Removes everything install.ps1 added.
$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LyrenneVR'
$layerJson = Join-Path $dest 'layer\XR_APILAYER_LYRENNE_wrist_player.json'
$layerKey = 'HKLM:\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit'

Get-Process LyrenneVR -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LyrenneVR' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Lyrenne VR.lnk') -ErrorAction SilentlyContinue

$cmd = "Remove-ItemProperty -Path '$layerKey' -Name '$layerJson' -ErrorAction SilentlyContinue"
Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd

Start-Sleep -Milliseconds 300
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Lyrenne VR removed. Settings and logs stay in %LOCALAPPDATA%\LyrenneVR.'
