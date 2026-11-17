@echo off
rem Opens the backend's TCP port and the raid UDP ports in Windows Firewall (see open_firewall_ports.ps1).
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0open_firewall_ports.ps1" %*
pause
