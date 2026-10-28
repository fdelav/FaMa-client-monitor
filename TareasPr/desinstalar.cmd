@echo off
rem Quita el cliente FaMa del usuario actual (sin Administrador).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0desinstalar.ps1" %*
pause
