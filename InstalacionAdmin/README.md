# Instalación con Administrador (servicio de Windows)

Variante **opcional** del cliente FaMa. Solo se usa si TIC la autoriza. La instalación normal (`instalar.cmd`, sin Administrador) sigue siendo la principal y no cambia.

## Qué cambia frente a la instalación sin Administrador

- Arranca junto con el equipo, sin esperar a que alguien inicie sesión.
- Si falla, Windows lo reinicia solo (3 intentos, 1 minuto entre cada uno).
- Los usuarios no pueden detenerlo, modificarlo ni cambiar su configuración: el programa queda en `Program Files` y sus datos en `ProgramData`, con permisos solo para Administradores.
- Hay un solo registro por PC, no uno por perfil de usuario.
- Por defecto corre con una cuenta de mínimos privilegios (`NT SERVICE\FaMaClientMonitor`). Con `-ComoSystem` corre como SYSTEM, lo que permite la temperatura real del CPU (además hace falta el driver de sensores, que este instalador **no** instala).

## Instalar

Genera el paquete como siempre (`TareaPr\publicar.ps1`); ya incluye `instalar-admin.ps1` y `desinstalar-admin.ps1`. Copia la carpeta `FaMa-cliente` al equipo.

Abre PowerShell **como administrador** (menú Inicio, clic derecho en PowerShell, *Ejecutar como administrador*) y sitúate en la carpeta del paquete:

```powershell
cd "ruta a FaMa-cliente"
```

Instala el servicio, con la dirección de tu API:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\instalar-admin.ps1 -ApiUrl https://direccion-de-tu-api
```

Al final debe decir `Servicio instalado y en ejecucion`.

Para correrlo como SYSTEM (temperatura real del CPU), agrega `-ComoSystem` al comando anterior.

## Comprobar que funciona

Estado del servicio:

```powershell
Get-Service FaMaClientMonitor
```

Debe mostrar `Running`. Mira el final del log:

```powershell
Get-ChildItem "$env:ProgramData\FaMa\logs" | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Tail 30
```

Deben aparecer líneas que empiezan con `Lectura`. Por último, reinicia el equipo y comprueba, **sin iniciar sesión**, que el servicio vuelve a estar en `Running`. Esa es la prueba que decide si sirve.

## Manejar el servicio

Detenerlo, iniciarlo o reiniciarlo:

```powershell
Stop-Service FaMaClientMonitor
Start-Service FaMaClientMonitor
Restart-Service FaMaClientMonitor
```

Pausarlo para que no vuelva a arrancar con el equipo:

```powershell
Stop-Service FaMaClientMonitor
Set-Service FaMaClientMonitor -StartupType Disabled
```

Reanudarlo:

```powershell
sc.exe config FaMaClientMonitor start= delayed-auto
Start-Service FaMaClientMonitor
```

Cambiar la dirección de la API, o actualizar a una versión nueva: genera el paquete otra vez y vuelve a ejecutar `instalar-admin.ps1` (con `-ApiUrl` si cambió la dirección). No se pierde la cola de lecturas ni el registro del PC.

## Desinstalar

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\desinstalar-admin.ps1
```

Por defecto conserva los datos. Con `-BorrarDatos` elimina también `%ProgramData%\FaMa`.

## Dónde queda cada cosa

- Programa y configuración: `%ProgramFiles%\FaMa\cliente` (solo un Administrador puede cambiarlos).
- Datos (cola local, registro del PC y logs): `%ProgramData%\FaMa`. Los usuarios normales no tienen acceso.
- Servicio: `FaMaClientMonitor`, con arranque automático diferido. También escribe en el Visor de eventos (registro *Aplicación*).

## Si el PC ya tenía la instalación por usuario

El instalador detiene las copias que estén abiertas. Sus entradas de arranque automático quedan, pero al abrirse se cierran solas, porque el servicio ya ocupa el lugar. Para limpiarlas del todo, ejecuta `desinstalar.cmd` en cada perfil.

## Solución rápida de problemas

- `Este script necesita Administrador`: abre PowerShell con *Ejecutar como administrador*.
- El servicio no queda en `Running`: mira el log en `%ProgramData%\FaMa\logs` y el Visor de eventos (*Aplicación*, origen `FaMaClientMonitor`).
- Un antivirus o AppLocker bloquea el `.exe` en `Program Files`: es una política del equipo; pide a TIC que lo permita.
- El servicio corre pero no llegan datos a la API: la dirección es incorrecta o la API está apagada. Mira el log y reinstala con `-ApiUrl`.
- La temperatura sale en 0 o casi constante: es lo esperado con la cuenta de mínimos privilegios. Para la temperatura real hace falta `-ComoSystem` y el driver de sensores.

## Para revisar con TIC

- Que autoricen un servicio con arranque automático y la cuenta con la que corre.
- Si se quiere temperatura real del CPU: que autoricen el driver de sensores. Es un controlador de kernel y se revisará con más cuidado.
- Si hay AppLocker o lista blanca de antivirus, y si exigen que el `.exe` y los scripts estén firmados.
- Que la API use https: con el servicio no hay un usuario delante que note que el envío va sin cifrar.
