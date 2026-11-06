@echo off
rem Tests every model on every device of this computer. Report: model_test_report.md in this folder.
rem Options: --models qwen3-8b,phi-3.5-mini  --devices NPU,GPU,CPU  --no-download  --quick
cd /d "%~dp0"
dotnet run -c Release --project "%~dp0ModelTests.csproj" -- %*
pause
