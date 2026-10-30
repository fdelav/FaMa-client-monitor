<#
.SYNOPSIS
    Reactiva el cliente FaMa despues de pausar.cmd. No requiere Administrador.

.DESCRIPTION
    Vuelve a registrar el arranque automatico del usuario y lo inicia ahora. Usa lo que ya
    esta instalado en %LOCALAPPDATA%\FaMa\cliente, asi que no hace falta el paquete. Si el
    cliente no esta instalado en este equipo, indica que hay que ejecutar instalar.cmd.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$nombre   = 'FaMaClientMonitor'
$claveRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$datosLocales = $env:LOCALAPPDATA
if (-not $datosLocales) { $datosLocales = [Environment]::GetFolderPath('LocalApplicationData') }
$destino = Join-Path $datosLocales 'FaMa\cliente'
$exe     = Join-Path $destino "$nombre.exe"
$vbs     = Join-Path $destino 'lanzar-oculto.vbs'

if (-not ((Test-Path $exe) -and (Test-Path $vbs))) {
    throw "El cliente no esta instalado en este equipo ($destino). Ejecuta instalar.cmd desde la carpeta del paquete."
}

$comando = 'wscript.exe //B //Nologo "{0}"' -f $vbs
Set-ItemProperty -Path $claveRun -Name $nombre -Value $comando

if (-not (Get-Process -Name $nombre -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath 'wscript.exe' -ArgumentList '//B', '//Nologo', "`"$vbs`""
    Start-Sleep -Seconds 5
}

Write-Host ''
$proceso = Get-Process -Name $nombre -ErrorAction SilentlyContinue
if ($proceso) {
    Write-Host "Cliente en ejecucion (PID $($proceso[0].Id)) y con arranque automatico." -ForegroundColor Green
} else {
    Write-Host 'Se activo el arranque automatico, pero el proceso no esta corriendo. Revisa el log en la carpeta logs del cliente.' -ForegroundColor Red
}
