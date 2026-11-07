@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="
for /f "usebackq tokens=*" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if "%VSPATH%"=="" (echo MSVC x64 build tools not found & exit /b 1)
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "OUT=%~dp0..\build\layer"
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /std:c++17 /O2 /MT /EHsc /W4 /wd4100 /LD /I"%~dp0..\third_party\openxr" "%~dp0layer.cpp" /Fo"%OUT%\\" /Fe"%OUT%\XR_APILAYER_LYRENNE_wrist_player.dll" /link /NOLOGO kernel32.lib || exit /b 1
copy /y "%~dp0XR_APILAYER_LYRENNE_wrist_player.json" "%OUT%\" >nul
echo built %OUT%\XR_APILAYER_LYRENNE_wrist_player.dll
