@echo off
rem Shows what the keyboard sends (Fn, lock/Touch ID, Eject). Log: tools\KeyProbe\bin\...\keyprobe.log
dotnet run -c Release --project "%~dp0tools\KeyProbe"
pause
