@echo off
rem Launcher for Deploy-RevitAddin.ps1 (for non-PowerShell shells like nu).
rem All arguments pass through, e.g.:
rem   deploy-revit-addin.bat -RevitYear 2025 -Configuration Release
rem   deploy-revit-addin.bat -Remove
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-RevitAddin.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-RevitAddin.ps1" %*
)
exit /b %errorlevel%
