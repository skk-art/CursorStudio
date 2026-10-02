@echo off
rem ============================================================
rem  CursorStudio build script (zero dependency: uses the
rem  csc.exe that ships with Windows .NET Framework 4.x)
rem  Usage: build.cmd   ->  dist\CursorStudio.exe
rem ============================================================
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo ERROR: csc.exe from .NET Framework 4.x not found
  exit /b 1
)

if not exist cursors\manifest.txt (
  echo [1/3] generating cursor assets: 5 schemes x 14 roles ...
  "%CSC%" -nologo -out:tools\GenCursors.exe -r:System.Drawing.dll -codepage:65001 tools\GenCursors.cs
  if errorlevel 1 exit /b 1
  tools\GenCursors.exe .
  if errorlevel 1 exit /b 1
)

echo [2/3] generating embedded resource list ...
if exist src\resources.rsp del src\resources.rsp
for /f "delims=" %%f in ('dir /b /s /a-d cursors') do call :res "%%f"

if not exist dist mkdir dist
echo [3/3] compiling ...
"%CSC%" -nologo -target:winexe -platform:AnyCPU -optimize+ -codepage:65001 -out:dist\CursorStudio.exe -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -win32manifest:src\app.manifest -win32icon:app.ico "@src\resources.rsp" src\CursorCore.cs src\Program.cs src\MainForm.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo DONE: dist\CursorStudio.exe
exit /b 0

:res
set name=%~1
set name=%name:*cursors\=%
set name=%name:\=_%
echo -res:"%~1","cs_%name%">>src\resources.rsp
goto :eof
