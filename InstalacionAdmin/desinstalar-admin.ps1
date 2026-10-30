<#
.SYNOPSIS
    Quita el servicio del cliente FaMa. REQUIERE ADMINISTRADOR.

.PARAMETER BorrarDatos
    Borra tambien la carpeta de datos (%ProgramData%\FaMa): cola local, registro del PC y logs.
    Por defecto se conservan.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\desinstalar-admin.ps1
#>
[CmdletBinding()]
param(
    [switch]$BorrarDatos
)

$ErrorActionPreference = 'Stop'

$nombre = 'FaMaClientMonitor'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Este script necesita Administrador. Abre PowerShell con "Ejecutar como administrador" y vuelve a ejecutarlo.'
}

$programFiles = $env:ProgramFiles
if (-not $programFiles) { $programFiles = [Environment]::GetFolderPath('ProgramFiles') }
$programData = $env:ProgramData
if (-not $programData) { $programData = [Environment]::GetFolderPath('CommonApplicationData') }

$destino = Join-Path $programFiles 'FaMa\cliente'
$datos   = Join-Path $programData 'FaMa'

$servicio = Get-Service -Name $nombre -ErrorAction SilentlyContinue
if ($servicio) {
    if ($servicio.Status -ne 'Stopped') {
        Stop-Service -Name $nombre -Force
        $servicio.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $nombre | Out-Null
}
Get-Process -Name $nombre -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $destino) { Remove-Item -Path $destino -Recurse -Force -ErrorAction SilentlyContinue }
$padre = Split-Path -Parent $destino
if ((Test-Path $padre) -and -not (Get-ChildItem $padre -Force | Select-Object -First 1)) { Remove-Item $padre -Force -ErrorAction SilentlyContinue }

if ($BorrarDatos -and (Test-Path $datos)) { Remove-Item -Path $datos -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Servicio del cliente FaMa eliminado.' -ForegroundColor Green
if (-not $BorrarDatos -and (Test-Path $datos)) { Write-Host "Se conservaron los datos en $datos (usa -BorrarDatos para quitarlos)." }
