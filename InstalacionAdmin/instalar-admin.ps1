<#
.SYNOPSIS
    Instala el cliente FaMa como SERVICIO DE WINDOWS. REQUIERE ADMINISTRADOR.

.DESCRIPTION
    Variante opcional, solo si TIC la autoriza. La instalacion por usuario (instalar.ps1) no
    necesita Administrador y sigue siendo la principal.

    Lo que hace:
      - Copia el programa a %ProgramFiles%\FaMa\cliente (carpeta que los usuarios NO pueden
        modificar).
      - Guarda los datos (cola local, registro del PC y logs) en %ProgramData%\FaMa, con
        permisos solo para SYSTEM, Administradores y la cuenta del servicio.
      - Crea el servicio "FaMaClientMonitor": arranca con el equipo (sin iniciar sesion) y se
        reinicia solo si falla.
      - Por defecto corre con una cuenta virtual de minimos privilegios (NT SERVICE\FaMaClientMonitor).
        Con -ComoSystem corre como SYSTEM, lo que permite la temperatura real del CPU (hace falta
        ademas el driver de sensores, que este script NO instala).

    Es repetible: ejecutarlo de nuevo actualiza los archivos y reinicia el servicio, sin perder la
    cola local ni el registro del PC.

    Ejecutar desde la carpeta del paquete (la que genera publicar.ps1), en un PowerShell abierto
    con "Ejecutar como administrador".

.PARAMETER Origen
    Carpeta con el paquete (FaMaClientMonitor.exe). Por defecto, la carpeta de este script.

.PARAMETER ApiUrl
    Direccion de la API para este equipo. Se guarda en appsettings.Production.json, junto al
    programa (solo un Administrador puede cambiarla).

.PARAMETER ComoSystem
    Ejecuta el servicio como SYSTEM en lugar de la cuenta virtual de minimos privilegios.

.PARAMETER Datos
    Carpeta de datos. Por defecto %ProgramData%\FaMa.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\instalar-admin.ps1 -ApiUrl https://xxxx.ngrok-free.dev
#>
[CmdletBinding()]
param(
    [string]$Origen,
    [string]$ApiUrl,
    [switch]$ComoSystem,
    [string]$Datos
)

$ErrorActionPreference = 'Stop'

$nombre = 'FaMaClientMonitor'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Este script instala un servicio de Windows y necesita Administrador. Abre PowerShell con "Ejecutar como administrador" y vuelve a ejecutarlo.'
}

$aqui = $PSScriptRoot
if (-not $aqui -and $MyInvocation.MyCommand.Path) { $aqui = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $aqui) { $aqui = (Get-Location).Path }
if (-not $Origen) { $Origen = $aqui }

$exeOrigen = Join-Path $Origen "$nombre.exe"
if (-not (Test-Path $exeOrigen)) {
    throw "No se encuentra $exeOrigen. Ejecuta este script desde el PAQUETE que genera publicar.ps1 (carpeta dist\FaMa-cliente), o pasa -Origen con esa carpeta."
}

$programFiles = $env:ProgramFiles
if (-not $programFiles) { $programFiles = [Environment]::GetFolderPath('ProgramFiles') }
$programData = $env:ProgramData
if (-not $programData) { $programData = [Environment]::GetFolderPath('CommonApplicationData') }

$destino = Join-Path $programFiles 'FaMa\cliente'
if (-not $Datos) { $Datos = Join-Path $programData 'FaMa' }
$exe = Join-Path $destino "$nombre.exe"

if ($ApiUrl) {
    $ApiUrl = $ApiUrl.Trim().TrimEnd('/')
    $uri = $null
    if (-not ([Uri]::TryCreate($ApiUrl, [UriKind]::Absolute, [ref]$uri) -and ($uri.Scheme -eq 'http' -or $uri.Scheme -eq 'https'))) {
        throw "ApiUrl no es una URL http/https valida: $ApiUrl"
    }
    if ($uri.Scheme -eq 'http' -and -not $uri.IsLoopback) {
        Write-Warning 'La API se usa por http (sin cifrar): la clave del equipo y las lecturas viajarian en claro por la red. Se recomienda https.'
    }
}

$servicio = Get-Service -Name $nombre -ErrorAction SilentlyContinue
if ($servicio -and $servicio.Status -ne 'Stopped') {
    Stop-Service -Name $nombre -Force
    $servicio.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
$copiasUsuario = Get-Process -Name $nombre -ErrorAction SilentlyContinue
if ($copiasUsuario) {
    Write-Warning 'Habia copias del cliente en ejecucion (instalacion por usuario): se detienen.'
    $copiasUsuario | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

New-Item -ItemType Directory -Force -Path $destino | Out-Null
Copy-Item -Path (Join-Path $Origen '*') -Destination $destino -Recurse -Force `
    -Exclude 'monitoreo.db', 'enrollment.dat', 'logs', 'appsettings.Production.json', '*.ps1', '*.cmd', '*.vbs', '*.pdb'

if ($ApiUrl) {
    $override = @{ FaMa = @{ ApiUrl = $ApiUrl } } | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText((Join-Path $destino 'appsettings.Production.json'), $override, (New-Object System.Text.UTF8Encoding($false)))
}
Get-ChildItem -Path $destino -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $Datos | Out-Null

$rutaServicio = '"{0}" --FaMa:DatosDir="{1}"' -f $exe, $Datos

if (-not (Get-Service -Name $nombre -ErrorAction SilentlyContinue)) {
    New-Service -Name $nombre -BinaryPathName $rutaServicio -DisplayName 'FaMa Client Monitor' `
        -Description 'Mide el estado del equipo (CPU, RAM, GPU, temperatura y disco) y lo envia a la API de FaMa.' `
        -StartupType Automatic | Out-Null
}
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$nombre" -Name ImagePath -Value $rutaServicio

& sc.exe config $nombre start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe config start fallo (codigo $LASTEXITCODE)." }

if ($ComoSystem) {
    & sc.exe config $nombre obj= LocalSystem | Out-Null
} else {
    & sc.exe config $nombre obj= "NT SERVICE\$nombre" | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw "sc.exe config obj fallo (codigo $LASTEXITCODE)." }

& sc.exe failure $nombre reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure fallo (codigo $LASTEXITCODE)." }

try {
    if (-not [System.Diagnostics.EventLog]::SourceExists($nombre)) {
        [System.Diagnostics.EventLog]::CreateEventSource($nombre, 'Application')
    }
} catch {
    Write-Warning "No se pudo registrar el origen de eventos: $($_.Exception.Message)"
}

$permisos = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
if (-not $ComoSystem) { $permisos += "NT SERVICE\${nombre}:(OI)(CI)M" }
$argumentosIcacls = @($Datos, '/inheritance:r')
foreach ($permiso in $permisos) { $argumentosIcacls += '/grant:r'; $argumentosIcacls += $permiso }
& icacls.exe @argumentosIcacls | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls fallo (codigo $LASTEXITCODE)." }

Start-Service -Name $nombre
Start-Sleep -Seconds 5
$estado = (Get-Service -Name $nombre).Status

Write-Host ''
if ($estado -eq 'Running') {
    Write-Host 'Servicio instalado y en ejecucion.' -ForegroundColor Green
} else {
    Write-Host "El servicio se instalo, pero su estado es: $estado. Revisa el log en $Datos\logs y el Visor de eventos (Aplicacion)." -ForegroundColor Red
}
Write-Host "Programa : $destino"
Write-Host "Datos    : $Datos"
Write-Host ("Cuenta   : {0}" -f $(if ($ComoSystem) { 'SYSTEM' } else { "NT SERVICE\$nombre (minimos privilegios)" }))
if ($ApiUrl) { Write-Host "ApiUrl   : $ApiUrl" }
Write-Host 'Para quitarlo: .\desinstalar-admin.ps1'
