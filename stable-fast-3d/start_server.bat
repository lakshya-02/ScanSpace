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
if "%SCANSPACE_HOST%"=="" set SCANSPACE_HOST=127.0.0.1
if "%SCANSPACE_PORT%"=="" set SCANSPACE_PORT=8000
if "%SCANSPACE_CACHE_ENABLED%"=="" set SCANSPACE_CACHE_ENABLED=true
if "%SCANSPACE_CACHE_DIR%"=="" set SCANSPACE_CACHE_DIR=%cd%\output\cache
for /r "%cd%" %%F in (*.meta) do del /f /q "%%F" >nul 2>&1
echo Host: %SCANSPACE_HOST%:%SCANSPACE_PORT%
echo Cache: %SCANSPACE_CACHE_DIR%
if not "%SCANSPACE_API_TOKEN%"=="" echo Auth: bearer token enabled
python server.py

pause
