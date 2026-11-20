@echo off
rem Builds KosShell.dll into shell\bin (x64, static CRT, C++/WinRT from the Windows SDK). Needs Visual Studio 2022 Build Tools (C++ workload).
rem "build.cmd probe": the test build, into shell\bin\probe: it logs to %TEMP%\KosShell-probe.txt and takes a few scrollbar track
rem rectangles of any XAML process as its parts, so the loading, the tree callbacks and the switch can be checked without the taskbar.
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -property installationPath`) do set VS=%%i
if not defined VS (echo Visual Studio Build Tools not found & exit /b 1)
set PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
set OUT=%~dp0bin
set DEFS=
if /i "%~1"=="probe" (set OUT=%~dp0bin\probe& set DEFS=/DKOS_SHELL_PROBE)
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo %DEFS% /O2 /MT /EHsc /W4 /std:c++17 /permissive- /LD "%~dp0KosShell.cpp" /Fe"%OUT%\KosShell.dll" /Fo"%OUT%\\" /link /DEF:"%~dp0KosShell.def" windowsapp.lib user32.lib ole32.lib oleaut32.lib || exit /b 1
