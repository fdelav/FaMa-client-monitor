<#
.SYNOPSIS
    Genera el paquete de instalacion del cliente FaMa (compilacion Release) en una carpeta fija.

.DESCRIPTION
    Se ejecuta en la maquina de desarrollo (la que tiene el SDK de .NET), NO en los equipos de las
    salas. Esta en la carpeta TareaPr del proyecto, pero genera el paquete de la raiz. Deja en dist\FaMa-cliente todo lo necesario para instalar sin Administrador:
    el cliente compilado en Release con el runtime de .NET incluido (self-contained), y los
    scripts instalar / desinstalar / probar-entorno.

.PARAMETER Salida
    Carpeta donde se genera el paquete. Por defecto dist\FaMa-cliente junto a este script.
    Se vacia en cada ejecucion para que no queden archivos viejos.

.PARAMETER ApiUrl
    Si se indica, se escribe en el appsettings.json DEL PAQUETE, de modo que todos los equipos
    donde se instale ya apunten a esa API. (Se puede cambiar por equipo con instalar.ps1 -ApiUrl.)

.PARAMETER Zip
    Ademas de la carpeta, genera dist\FaMa-cliente.zip para llevarlo en una sola pieza.

.EXAMPLE
    .\publicar.ps1 -ApiUrl https://xxxx.ngrok-free.dev -Zip
#>
[CmdletBinding()]
param(
    [string]$Salida,
    [string]$ApiUrl,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

# Carpeta de este script ($PSScriptRoot a veces llega vacio; se usa un plan B).
$aqui = $PSScriptRoot
if (-not $aqui -and $MyInvocation.MyCommand.Path) { $aqui = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $aqui) { $aqui = (Get-Location).Path }

# Raiz del proyecto: la carpeta que tiene FaMaClientMonitor.csproj. Los scripts viven en la
# subcarpeta TareaPr, asi que se busca en esta carpeta y en la anterior.
$raiz = $aqui
if (-not (Test-Path (Join-Path $raiz 'FaMaClientMonitor.csproj'))) { $raiz = Split-Path -Parent $aqui }
if (-not $raiz -or -not (Test-Path (Join-Path $raiz 'FaMaClientMonitor.csproj'))) {
    throw "No se encuentra FaMaClientMonitor.csproj ni en $aqui ni en la carpeta anterior. Este script debe estar en la raiz del proyecto o en una subcarpeta (TareaPr)."
}

if (-not $Salida) { $Salida = Join-Path $raiz 'dist\FaMa-cliente' }
Set-Location $raiz

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No se encuentra dotnet. Este script va en la maquina de desarrollo (con el SDK de .NET), no en los equipos de las salas.'
}

if ($ApiUrl) {
    $ApiUrl = $ApiUrl.Trim().TrimEnd('/')
    $uri = $null
    if (-not ([Uri]::TryCreate($ApiUrl, [UriKind]::Absolute, [ref]$uri) -and ($uri.Scheme -eq 'http' -or $uri.Scheme -eq 'https'))) {
        throw "ApiUrl no es una URL http/https valida: $ApiUrl"
    }
}

# Limpieza de la carpeta de salida, solo si esta vacia o es un paquete anterior (nunca una carpeta ajena).
if ((Test-Path $Salida) -and (Get-ChildItem $Salida -Force | Select-Object -First 1)) {
    if (-not (Test-Path (Join-Path $Salida 'FaMaClientMonitor.exe'))) {
        throw "$Salida no esta vacia y no parece un paquete anterior; no se borra."
    }
    Remove-Item $Salida -Recurse -Force
}

# Release, con el runtime incluido: en los equipos institucionales no se puede instalar .NET.
dotnet publish (Join-Path $raiz 'FaMaClientMonitor.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $Salida
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (codigo $LASTEXITCODE)." }

foreach ($archivo in 'instalar.ps1', 'instalar.cmd', 'desinstalar.ps1', 'desinstalar.cmd', 'pausar.ps1', 'pausar.cmd', 'reanudar.ps1', 'reanudar.cmd', 'probar-entorno.ps1') {
    # Los scripts estan junto a este (TareaPr); si alguno se dejo en la raiz, tambien se encuentra.
    $ruta = Join-Path $aqui $archivo
    if (-not (Test-Path $ruta)) { $ruta = Join-Path $raiz $archivo }
    if (Test-Path $ruta) { Copy-Item $ruta $Salida -Force } else { Write-Warning "No se encontro $archivo; no se incluye en el paquete." }
}

# Instalacion con Administrador (servicio de Windows): rama instalacion-admin. Va en el paquete
# aparte de los demas scripts; solo se usa si TIC la autoriza.
$carpetaAdmin = Join-Path $raiz 'InstalacionAdmin'
foreach ($archivo in 'instalar-admin.ps1', 'desinstalar-admin.ps1') {
    $ruta = Join-Path $carpetaAdmin $archivo
    if (Test-Path $ruta) { Copy-Item $ruta $Salida -Force } else { Write-Warning "No se encontro $archivo en InstalacionAdmin; no se incluye en el paquete." }
}
$rutaLeemeAdmin = Join-Path $carpetaAdmin 'README.md'
if (Test-Path $rutaLeemeAdmin) { Copy-Item $rutaLeemeAdmin (Join-Path $Salida 'README-admin.md') -Force }

if ($ApiUrl) {
    $ruta = Join-Path $Salida 'appsettings.json'
    $texto = [System.IO.File]::ReadAllText($ruta)
    $nuevo = $texto -replace '("ApiUrl"\s*:\s*")[^"]*(")', ('${1}' + $ApiUrl + '${2}')
    if ($nuevo -eq $texto -and $texto -notmatch [regex]::Escape($ApiUrl)) { throw 'No se pudo ajustar ApiUrl en appsettings.json del paquete.' }
    [System.IO.File]::WriteAllText($ruta, $nuevo, (New-Object System.Text.UTF8Encoding($false)))
}

if ($Zip) {
    $rutaZip = "$Salida.zip"
    if (Test-Path $rutaZip) { Remove-Item $rutaZip -Force }
    Compress-Archive -Path (Join-Path $Salida '*') -DestinationPath $rutaZip
}

$tamano = (Get-ChildItem $Salida -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
Write-Host ('Paquete listo en {0} ({1:N0} MB).' -f $Salida, $tamano) -ForegroundColor Green
if ($Zip) { Write-Host "Zip: $rutaZip" }
Write-Host 'En cada equipo: copiar la carpeta, correr probar-entorno.ps1 (opcional) y luego instalar.cmd'
