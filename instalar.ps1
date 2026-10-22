<#
.SYNOPSIS
    Instala el cliente FaMa para el usuario actual, SIN permisos de Administrador.

.DESCRIPTION
    Copia el cliente a %LOCALAPPDATA%\FaMa\cliente, crea un lanzador oculto y lo registra para que
    arranque al iniciar sesion (clave HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
    NO crea servicios, ni tareas programadas, ni instala drivers: todo queda dentro del perfil
    del usuario, asi que no hace falta elevar.

    El paquete a instalar lo genera publicar.ps1 en la maquina de desarrollo (incluye el runtime
    de .NET, porque en los equipos institucionales no se puede instalar). Se copia la carpeta
    completa al equipo (USB o carpeta compartida) y se ejecuta instalar.cmd, o este script.

    Es repetible: ejecutarlo de nuevo actualiza los archivos y reinicia el cliente, sin perder la
    cola local (monitoreo.db), el enrolamiento ni los logs.

.PARAMETER Origen
    Carpeta con el paquete (FaMaClientMonitor.exe y appsettings.json).
    Por defecto, la carpeta donde esta este script.

.PARAMETER ApiUrl
    Direccion de la API para ESTE equipo (por ejemplo https://xxxx.ngrok-free.dev). Se guarda en
    appsettings.Production.json, que tiene prioridad sobre appsettings.json y no se pisa al
    reinstalar. Si se omite, se usa la que trae el paquete.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -ApiUrl https://xxxx.ngrok-free.dev
#>
[CmdletBinding()]
param(
    [string]$Origen,
    [string]$ApiUrl
)

$ErrorActionPreference = 'Stop'

# Carpeta de este script. $PSScriptRoot a veces llega vacio (segun como se lance el script),
# asi que se calcula con un plan B en lugar de usarlo como valor por defecto del parametro.
$aqui = $PSScriptRoot
if (-not $aqui -and $MyInvocation.MyCommand.Path) { $aqui = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $aqui) { $aqui = (Get-Location).Path }
if (-not $Origen) { $Origen = $aqui }

$datosLocales = $env:LOCALAPPDATA
if (-not $datosLocales) { $datosLocales = [Environment]::GetFolderPath('LocalApplicationData') }

$nombre    = 'FaMaClientMonitor'
$destino   = Join-Path $datosLocales 'FaMa\cliente'
$exeOrigen = Join-Path $Origen "$nombre.exe"
$claveRun  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if (-not (Test-Path $exeOrigen)) {
    throw "No se encuentra $exeOrigen. instalar debe ejecutarse desde el PAQUETE que genera publicar.ps1 (carpeta dist\FaMa-cliente), no desde el codigo fuente. Tambien puedes pasar -Origen con la carpeta del paquete."
}

if ($ApiUrl) {
    $ApiUrl = $ApiUrl.Trim().TrimEnd('/')
    $uri = $null
    if (-not ([Uri]::TryCreate($ApiUrl, [UriKind]::Absolute, [ref]$uri) -and ($uri.Scheme -eq 'http' -or $uri.Scheme -eq 'https'))) {
        throw "ApiUrl no es una URL http/https valida: $ApiUrl"
    }
}

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($esAdmin) {
    Write-Warning 'Esta consola es de Administrador: se instalara igual, pero solo para ESTE usuario. Para instalar como lo hara el equipo real, usa una cuenta normal.'
}

# 1) Si ya hay una copia corriendo de este usuario, se detiene para poder reemplazar los archivos.
Get-Process -Name $nombre -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2) Copia de archivos. No se pisan la cola local (monitoreo.db), el enrolamiento ni los logs.
New-Item -ItemType Directory -Force -Path $destino | Out-Null
Copy-Item -Path (Join-Path $Origen '*') -Destination $destino -Recurse -Force `
    -Exclude 'monitoreo.db', 'enrollment.dat', 'logs', 'appsettings.Production.json', '*.ps1', '*.cmd', '*.pdb'

# Ajuste de la URL de la API para este equipo, sin tocar appsettings.json del paquete.
if ($ApiUrl) {
    $override = @{ FaMa = @{ ApiUrl = $ApiUrl } } | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText((Join-Path $destino 'appsettings.Production.json'), $override, (New-Object System.Text.UTF8Encoding($false)))
}

$exe = Join-Path $destino "$nombre.exe"

# 3) Lanzador oculto. El cliente es una aplicacion de consola; wscript la abre sin ventana.
#    (Unicode: la ruta del perfil puede tener tildes.)
$vbs = Join-Path $destino 'lanzar-oculto.vbs'
$lineas = @(
    'Set sh = CreateObject("WScript.Shell")',
    ('sh.CurrentDirectory = "{0}"' -f $destino),
    ('sh.Run """{0}""", 0, False' -f $exe)
)
Set-Content -Path $vbs -Value $lineas -Encoding Unicode

# 4) Arranque automatico al iniciar sesion de ESTE usuario.
$comando = 'wscript.exe //B //Nologo "{0}"' -f $vbs
Set-ItemProperty -Path $claveRun -Name $nombre -Value $comando

# 5) Arrancarlo ahora, sin esperar al proximo inicio de sesion.
Start-Process -FilePath 'wscript.exe' -ArgumentList '//B', '//Nologo', "`"$vbs`""
Start-Sleep -Seconds 5

$proceso = Get-Process -Name $nombre -ErrorAction SilentlyContinue
Write-Host ''
if ($proceso) {
    Write-Host "Instalado y en ejecucion (PID $($proceso[0].Id))." -ForegroundColor Green
} else {
    Write-Host 'Se instalo, pero el proceso no esta corriendo. Posibles causas: el .exe esta bloqueado por AppLocker/antivirus, o wscript esta deshabilitado.' -ForegroundColor Red
}
Write-Host "Carpeta : $destino"
if ($ApiUrl) { Write-Host "ApiUrl  : $ApiUrl" }
Write-Host "Logs    : $(Join-Path $destino 'logs')"
Write-Host 'Para quitarlo: .\desinstalar.ps1'
