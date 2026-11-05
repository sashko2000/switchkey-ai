@echo off
rem Checks layout detection quality on dictionary words
dotnet run -c Release --project "%~dp0tests\DetectorTests"
pause
