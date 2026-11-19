@echo off
rem Builds KosEqApo.dll and its test host into apo\bin (x64, static CRT). Needs Visual Studio 2022 Build Tools (C++ workload).
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -property installationPath`) do set VS=%%i
if not defined VS (echo Visual Studio Build Tools not found & exit /b 1)
set PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
set OUT=%~dp0bin
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /O2 /MT /EHsc /W4 /std:c++17 /LD "%~dp0KosEqApo.cpp" /Fe"%OUT%\KosEqApo.dll" /Fo"%OUT%\\" /link /DEF:"%~dp0KosEqApo.def" audioeng.lib ole32.lib advapi32.lib || exit /b 1
cl /nologo /O2 /MT /EHsc /W4 /std:c++17 "%~dp0ApoHost.cpp" /Fe"%OUT%\ApoHost.exe" /Fo"%OUT%\\" /link ole32.lib advapi32.lib || exit /b 1
