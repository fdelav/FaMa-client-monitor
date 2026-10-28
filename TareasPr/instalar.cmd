@echo off
rem Instala el cliente FaMa para el usuario actual (sin Administrador).
rem Se puede pasar la API: instalar.cmd -ApiUrl https://xxxx.ngrok-free.dev
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0instalar.ps1" %*
pause
