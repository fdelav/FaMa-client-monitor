<#
.SYNOPSIS
    Comprueba, SIN permisos de Administrador, que permite este equipo lo que necesita el cliente FaMa.

.DESCRIPTION
    Pensado para correrse una vez en un equipo institucional, con una cuenta normal, ANTES de
    instalar. No instala nada ni cambia la configuracion del equipo: solo crea y borra archivos
    de prueba en %LOCALAPPDATA%\FaMa y una clave temporal en HKCU.

    Comprueba: politicas de PowerShell, escritura en la carpeta del usuario, si el perfil
    conserva archivos entre sesiones, la clave de arranque automatico, el lanzador oculto
    (wscript), la red hacia la API (filtros, proxy) y si hay temperatura legible sin Administrador.

.PARAMETER ApiUrl
    URL base de la API. Si se omite, se toma de appsettings.json junto a este script.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\probar-entorno.ps1 -ApiUrl "https://xxxx.ngrok-free.dev"
#>
[CmdletBinding()]
param(
    [string]$ApiUrl
)

$resultados = New-Object System.Collections.Generic.List[object]

function Registrar([string]$nombre, [string]$estado, [string]$detalle) {
    $resultados.Add([pscustomobject]@{ Prueba = $nombre; Estado = $estado; Detalle = $detalle })
    $color = switch ($estado) { 'OK' { 'Green' } 'FALLA' { 'Red' } default { 'Yellow' } }
    Write-Host ('[{0,-5}] {1}: {2}' -f $estado, $nombre, $detalle) -ForegroundColor $color
}

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$textoAdmin = if ($esAdmin) { 'si' } else { 'no' }
Write-Host "Equipo: $env:COMPUTERNAME | Usuario: $env:USERNAME | Administrador: $textoAdmin | PowerShell $($PSVersionTable.PSVersion)"
if ($esAdmin) {
    Write-Warning 'Esta consola es de Administrador. Las pruebas valen solo con una cuenta normal.'
}
Write-Host ''

$modo = $ExecutionContext.SessionState.LanguageMode
if ($modo -eq 'FullLanguage') {
    Registrar 'PowerShell' 'OK' 'Modo de lenguaje completo.'
} else {
    Registrar 'PowerShell' 'AVISO' "Modo de lenguaje: $modo (hay AppLocker o WDAC; los scripts pueden estar limitados)."
}

$politicaGrupo = @(Get-ExecutionPolicy -List | Where-Object { ($_.Scope -eq 'MachinePolicy' -or $_.Scope -eq 'UserPolicy') -and $_.ExecutionPolicy -ne 'Undefined' })
if ($politicaGrupo.Count -gt 0) {
    $lista = ($politicaGrupo | ForEach-Object { "$($_.Scope)=$($_.ExecutionPolicy)" }) -join ', '
    Registrar 'Politica de scripts' 'AVISO' "Politica de grupo ($lista). -ExecutionPolicy Bypass no la anula."
} else {
    Registrar 'Politica de scripts' 'OK' 'Ninguna politica de grupo bloquea scripts.'
}

$carpeta = Join-Path $env:LOCALAPPDATA 'FaMa\prueba'
try {
    New-Item -ItemType Directory -Force -Path $carpeta | Out-Null
    Set-Content -Path (Join-Path $carpeta 'x.txt') -Value 'prueba'
    Registrar 'Carpeta del usuario' 'OK' "Se puede escribir en $env:LOCALAPPDATA."
} catch {
    Registrar 'Carpeta del usuario' 'FALLA' $_.Exception.Message
}

$marca = Join-Path $env:LOCALAPPDATA 'FaMa\marca-perfil.txt'
if (Test-Path $marca) {
    $creada = (Get-Content $marca -Raw).Trim()
    Registrar 'Perfil persistente' 'OK' "La marca de una prueba anterior sigue ahi (creada $creada)."
} else {
    Set-Content -Path $marca -Value (Get-Date -Format 's') -ErrorAction SilentlyContinue
    Registrar 'Perfil persistente' 'AVISO' 'Primera prueba: cierra sesion, vuelve a entrar y ejecuta este script otra vez. Si la marca desaparece, el perfil se borra al cerrar sesion y lo instalado se pierde.'
}

$claveRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
try {
    Set-ItemProperty -Path $claveRun -Name 'FaMaPrueba' -Value 'cmd.exe /c exit'
    Remove-ItemProperty -Path $claveRun -Name 'FaMaPrueba'
    Registrar 'Arranque automatico' 'OK' 'Se puede escribir en la clave Run. (No garantiza que Windows la ejecute: una politica puede ignorarla; se confirma cerrando sesion y entrando.)'
} catch {
    Registrar 'Arranque automatico' 'FALLA' $_.Exception.Message
}

$vbs = Join-Path $carpeta 'p.vbs'
try {
    Set-Content -Path $vbs -Value 'WScript.Quit 7' -Encoding Ascii
    $p = Start-Process -FilePath 'wscript.exe' -ArgumentList '//B', '//Nologo', "`"$vbs`"" -PassThru -Wait -WindowStyle Hidden
    if ($p.ExitCode -eq 7) {
        Registrar 'Lanzador oculto (wscript)' 'OK' 'Windows Script Host funciona.'
    } else {
        Registrar 'Lanzador oculto (wscript)' 'FALLA' "wscript termino con codigo $($p.ExitCode): Windows Script Host esta deshabilitado o bloqueado. Hace falta otro lanzador."
    }
} catch {
    Registrar 'Lanzador oculto (wscript)' 'FALLA' $_.Exception.Message
}

if (-not $ApiUrl) {
    $aqui = $PSScriptRoot; if (-not $aqui -and $MyInvocation.MyCommand.Path) { $aqui = Split-Path -Parent $MyInvocation.MyCommand.Path }; if (-not $aqui) { $aqui = (Get-Location).Path }
    $cfg = Join-Path $aqui 'appsettings.json'
    if (Test-Path $cfg) {
        try { $ApiUrl = (Get-Content $cfg -Raw -Encoding UTF8 | ConvertFrom-Json).FaMa.ApiUrl } catch { }
    }
}
if (-not $ApiUrl) {
    Registrar 'Red hacia la API' 'AVISO' 'Sin URL: pasa -ApiUrl o deja appsettings.json junto al script.'
} else {
    try {
        $r = Invoke-WebRequest -Uri $ApiUrl -UseBasicParsing -TimeoutSec 15 -ProxyUseDefaultCredentials -Headers @{ 'ngrok-skip-browser-warning' = 'true' }
        Registrar 'Red hacia la API' 'OK' "Respondio HTTP $($r.StatusCode)."
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $codigo = [int]$resp.StatusCode
            if ($codigo -eq 407) {
                Registrar 'Red hacia la API' 'FALLA' 'El proxy de la red pide credenciales (407).'
            } else {
                Registrar 'Red hacia la API' 'OK' "Se llega a la direccion (HTTP $codigo). Que la API responda bien depende de que API, Postgres y ngrok esten encendidos."
            }
        } else {
            Registrar 'Red hacia la API' 'FALLA' "No se llega a $ApiUrl : $($_.Exception.Message). Puede ser un filtro de red que bloquea ngrok o un proxy."
        }
    }
}

try {
    $zonas = @(Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction Stop)
    if ($zonas.Count -gt 0) {
        $texto = ($zonas | ForEach-Object { '{0}: {1:N1} C' -f $_.Name, ($_.Temperature - 273.15) }) -join '; '
        Registrar 'Temperatura (zona termica)' 'OK' ("$texto  (zona termica del sistema, no necesariamente el CPU)")
    } else {
        Registrar 'Temperatura (zona termica)' 'AVISO' 'Sin zonas termicas legibles: la temperatura se enviara como 0.'
    }
} catch {
    Registrar 'Temperatura (zona termica)' 'AVISO' "No se pudo leer: $($_.Exception.Message). La temperatura se enviara como 0."
}

Remove-Item -Path $carpeta -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
$fallas = @($resultados | Where-Object { $_.Estado -eq 'FALLA' })
if ($fallas.Count -eq 0) {
    Write-Host 'Ninguna prueba fallo. Falta confirmar que el .exe se deja ejecutar desde %LOCALAPPDATA% (se ve al instalar).' -ForegroundColor Green
} else {
    Write-Host "$($fallas.Count) prueba(s) fallaron: $(($fallas | ForEach-Object { $_.Prueba }) -join ', ')" -ForegroundColor Red
}
