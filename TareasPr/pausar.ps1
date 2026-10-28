<#
.SYNOPSIS
    Pausa el cliente FaMa en este equipo, sin desinstalarlo. No requiere Administrador.

.DESCRIPTION
    Detiene el cliente y quita su arranque automatico, para que NO vuelva a iniciar al abrir
    sesion. No borra nada: la carpeta, el registro del PC (enrollment.dat), la cola local
    (monitoreo.db) y los logs quedan intactos. Para volver a activarlo: reanudar.cmd.

    Mientras esta en pausa no se toman lecturas (queda un hueco en el historial) y el PC
    aparece como inactivo en la API.
#>
[CmdletBinding()]
param()

$nombre   = 'FaMaClientMonitor'
$claveRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# 1) Detener el cliente de este usuario.
$procesos = Get-Process -Name $nombre -ErrorAction SilentlyContinue
if ($procesos) {
    $procesos | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# 2) Quitar el arranque automatico (sin borrar archivos).
Remove-ItemProperty -Path $claveRun -Name $nombre -ErrorAction SilentlyContinue

Write-Host ''
if (Get-Process -Name $nombre -ErrorAction SilentlyContinue) {
    Write-Host 'No se pudo detener el cliente. Pruebalo desde el Administrador de tareas.' -ForegroundColor Red
} else {
    Write-Host 'Cliente en pausa: detenido y sin arranque automatico.' -ForegroundColor Green
    Write-Host 'Para volver a activarlo: reanudar.cmd'
}
