<#
.SYNOPSIS
    Quita el cliente FaMa del usuario actual. No requiere Administrador.

.PARAMETER ConservarDatos
    No borra la carpeta %LOCALAPPDATA%\FaMa\cliente (deja cola local, enrolamiento y logs).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\desinstalar.ps1
#>
[CmdletBinding()]
param(
    [switch]$ConservarDatos
)

$nombre   = 'FaMaClientMonitor'
$datosLocales = $env:LOCALAPPDATA
if (-not $datosLocales) { $datosLocales = [Environment]::GetFolderPath('LocalApplicationData') }
$destino  = Join-Path $datosLocales 'FaMa\cliente'
$claveRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process -Name $nombre -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Remove-ItemProperty -Path $claveRun -Name $nombre -ErrorAction SilentlyContinue

if (-not $ConservarDatos -and (Test-Path $destino)) {
    Remove-Item -Path $destino -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Cliente FaMa detenido y quitado del arranque.' -ForegroundColor Green
if ($ConservarDatos) { Write-Host "Se conservo la carpeta $destino" }
