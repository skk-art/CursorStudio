@echo off
rem compile DogCursorGen together with CursorCore (reuse BuildCurBytes)
cd /d "%~dp0.."
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -out:tools\DogCursorGen.exe -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -codepage:65001 tools\DogCursorGen.cs src\CursorCore.cs
