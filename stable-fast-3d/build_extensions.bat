@echo off
setlocal
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0build_extensions.ps1"
exit /b %errorlevel%
