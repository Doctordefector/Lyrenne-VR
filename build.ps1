# Builds everything and lays out a ready-to-install folder in dist\LyrenneVR.
#   .\build.ps1          build and package
#   .\build.ps1 -Test    also run the self-test and the headless OpenXR end-to-end test
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist\LyrenneVR'

Write-Host '== OpenXR layer'
& (Join-Path $root 'layer\build.bat') | Where-Object { $_ -notmatch 'vswhere|operable program' }
if ($LASTEXITCODE) { throw 'layer build failed' }

Write-Host '== host'
dotnet publish (Join-Path $root 'host\LyrenneVR.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $root 'build\host') -v quiet -nologo
if ($LASTEXITCODE) { throw 'host build failed' }

if ($Test) {
    Write-Host '== tests'
    & (Join-Path $root 'test\build.bat') | Where-Object { $_ -notmatch 'vswhere|operable program' }
    if ($LASTEXITCODE) { throw 'test build failed' }
    $exe = Join-Path $root 'build\host\LyrenneVR.exe'
    & $exe --selftest | Out-Host
    if ($LASTEXITCODE) { throw 'self-test failed' }
    Get-Process LyrenneVR -ErrorAction SilentlyContinue | Stop-Process -Force
    $hostProc = Start-Process $exe -ArgumentList '--always' -PassThru
    try {
        Start-Sleep 3
        foreach ($api in 'd3d11', 'd3d12', 'd3d11-deadclick') {
            & (Join-Path $root 'build\test\xrtest.exe') $api (Join-Path $root "build\test\$api.bmp") | Out-Host
            if ($LASTEXITCODE) { throw "OpenXR $api test failed" }
        }
    } finally {
        Stop-Process $hostProc -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== package'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
Copy-Item (Join-Path $root 'build\host\*') $dist -Recurse
New-Item -ItemType Directory (Join-Path $dist 'layer') | Out-Null
Copy-Item (Join-Path $root 'build\layer\XR_APILAYER_LYRENNE_wrist_player.dll'), (Join-Path $root 'build\layer\XR_APILAYER_LYRENNE_wrist_player.json') (Join-Path $dist 'layer')
Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1'), (Join-Path $root 'README.md') $dist
Get-ChildItem $dist -Filter *.pdb -Recurse | Remove-Item
Write-Host "packaged $dist"
