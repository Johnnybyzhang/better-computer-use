@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "install_result=%ERRORLEVEL%"
echo.
pause
exit /b %install_result%
