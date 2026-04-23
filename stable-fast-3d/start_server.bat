@echo off
echo Starting Stable Fast 3D API Server...
echo.

cd /d "%~dp0"
if exist venv311\Scripts\activate.bat (
  call venv311\Scripts\activate.bat
) else if exist .venv\Scripts\activate.bat (
  call .venv\Scripts\activate.bat
) else if exist .venv312\Scripts\activate.bat (
  call .venv312\Scripts\activate.bat
) else (
  echo No supported virtual environment found.
  echo Expected one of: venv311, .venv, .venv312
  pause
  exit /b 1
)
python server.py

pause
