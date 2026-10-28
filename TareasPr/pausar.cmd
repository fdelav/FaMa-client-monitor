@echo off
rem Pausa el cliente FaMa: lo detiene y quita el arranque automatico, sin desinstalar.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pausar.ps1" %*
pause
