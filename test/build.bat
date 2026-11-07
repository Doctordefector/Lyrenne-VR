@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="
for /f "usebackq tokens=*" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if "%VSPATH%"=="" (echo MSVC x64 build tools not found & exit /b 1)
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "OUT=%~dp0..\build\test"
set "INC=%~dp0..\third_party\openxr"
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /std:c++17 /O2 /MT /EHsc /W3 /LD /I"%INC%" "%~dp0mock_runtime.cpp" /Fo"%OUT%\\" /Fe"%OUT%\mock_runtime.dll" /link /NOLOGO || exit /b 1
cl /nologo /std:c++17 /O2 /MT /EHsc /W3 /I"%INC%" "%~dp0xrtest.cpp" /Fo"%OUT%\\" /Fe"%OUT%\xrtest.exe" /link /NOLOGO "%~dp0..\third_party\openxr-loader\openxr_loader.lib" d3d11.lib || exit /b 1
copy /y "%~dp0mock_runtime.json" "%OUT%\" >nul
copy /y "%~dp0..\third_party\openxr-loader\openxr_loader.dll" "%OUT%\" >nul
echo built %OUT%\xrtest.exe
