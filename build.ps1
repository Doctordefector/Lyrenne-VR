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
    $hostProc = Start-Process $exe -ArgumentList '--always', '--defaults' -PassThru
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

Write-Host '== installer'
# Self-contained, so people need no .NET runtime; the setup exe carries it all in one file.
$sc = Join-Path $root 'build\host-sc'
if (Test-Path $sc) { Remove-Item $sc -Recurse -Force }
dotnet publish (Join-Path $root 'host\LyrenneVR.csproj') -c Release -r win-x64 --self-contained true -o $sc -v quiet -nologo
if ($LASTEXITCODE) { throw 'self-contained host build failed' }
Get-ChildItem $sc -Filter *.pdb -Recurse | Remove-Item
New-Item -ItemType Directory (Join-Path $sc 'layer') -Force | Out-Null
Copy-Item (Join-Path $root 'build\layer\XR_APILAYER_LYRENNE_wrist_player.dll'), (Join-Path $root 'build\layer\XR_APILAYER_LYRENNE_wrist_player.json') (Join-Path $sc 'layer')
$uninst = Join-Path $root 'build\uninstaller'
dotnet build (Join-Path $root 'installer\Setup.csproj') -c Release -p:Uninstaller=true -o $uninst -v quiet -nologo
if ($LASTEXITCODE) { throw 'uninstaller build failed' }
Copy-Item (Join-Path $uninst 'Uninstall.exe') $sc
$payload = Join-Path $root 'build\payload.zip'
if (Test-Path $payload) { Remove-Item $payload }
# Written entry by entry: Windows PowerShell's ZipFile.CreateFromDirectory names entries with
# backslashes ("layer\x.dll"), which is not a valid zip path.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipStream = [System.IO.File]::Open($payload, 'CreateNew')
$zip = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $scFull = (Resolve-Path $sc).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem $sc -Recurse -File) {
        $name = $f.FullName.Substring($scFull.Length).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zip.Dispose()
    $zipStream.Dispose()
}
$version = ([xml](Get-Content (Join-Path $root 'host\LyrenneVR.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
$setupOut = Join-Path $root 'build\setup'
dotnet build (Join-Path $root 'installer\Setup.csproj') -c Release -o $setupOut -v quiet -nologo
if ($LASTEXITCODE) { throw 'installer build failed' }
New-Item -ItemType Directory (Join-Path $root 'dist') -Force | Out-Null
$setupExe = Join-Path $root "dist\LyrenneVR-Setup-$version.exe"
Copy-Item (Join-Path $setupOut 'LyrenneVR-Setup.exe') $setupExe -Force
Write-Host ("installer {0} ({1:N0} MB)" -f $setupExe, ((Get-Item $setupExe).Length / 1MB))

Write-Host '== package'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
Copy-Item (Join-Path $root 'build\host\*') $dist -Recurse
New-Item -ItemType Directory (Join-Path $dist 'layer') | Out-Null
Copy-Item (Join-Path $root 'build\layer\XR_APILAYER_LYRENNE_wrist_player.dll'), (Join-Path $root 'build\layer\XR_APILAYER_LYRENNE_wrist_player.json') (Join-Path $dist 'layer')
Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1'), (Join-Path $root 'README.md') $dist
Get-ChildItem $dist -Filter *.pdb -Recurse | Remove-Item
Write-Host "packaged $dist"
