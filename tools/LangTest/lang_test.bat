@echo off
rem Checks how well the model knows each language (forward and back translation). Report: lang_test_report.txt
cd /d "%~dp0"
chcp 65001 >nul
set PY=..\..\..\npu\.venv\Scripts\python.exe
if not exist "%PY%" (
  echo Python environment from the npu folder not found: %PY%
  pause
  exit /b 1
)
"%PY%" lang_test.py %*
pause
