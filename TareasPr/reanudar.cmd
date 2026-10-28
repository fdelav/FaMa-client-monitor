@echo off
rem Reactiva el cliente FaMa despues de pausar.cmd.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0reanudar.ps1" %*
pause
