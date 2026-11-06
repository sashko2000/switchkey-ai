@echo off
rem Unit tests (the same ones Visual Studio shows in Test Explorer): layout detection and model logic
dotnet test "%~dp0tests\KeyboardSwitcher.Tests" -c Release --logger "console;verbosity=normal"
pause
