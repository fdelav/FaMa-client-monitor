# FaMa Client Monitor

Programa para Windows que mide el estado de un PC (CPU, RAM, GPU, temperatura y disco) y lo envía a la API de FaMa. Se instala en cada PC de las salas de cómputo.

Funciona **sin permisos de Administrador**: no es un servicio de Windows ni instala drivers. Se instala dentro del perfil del usuario y arranca cuando esa persona inicia sesión.

## Instalar el cliente en un PC con PowerShell

Abre PowerShell. Los pasos 1 y 2 se hacen una sola vez; el paso 3 en cada PC.

### 1. Generar el paquete (en la máquina de desarrollo)

Necesitas el SDK de .NET 10. Comprueba que está instalado:

```powershell
dotnet --version
```

Debe mostrar una versión que empiece por `10`. Sitúate en la raíz del proyecto (la carpeta que tiene `FaMaClientMonitor.csproj`; los scripts están en la subcarpeta `TareaPr`):

```powershell
cd "ruta al proyecto"
```

Genera el paquete, cambiando la dirección por la de tu API (la de ngrok, o `http://localhost:5000` si corre en el mismo PC):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\TareaPr\publicar.ps1 -ApiUrl https://direccion-de-tu-api
```

Al terminar queda la carpeta `dist\FaMa-cliente`. Ya incluye todo lo necesario, así que los PC de las salas no necesitan instalar nada.

### 2. Llevar el paquete al PC

Copia la carpeta `FaMa-cliente` al equipo (USB o carpeta compartida).

### 3. Instalar en el PC

Con una cuenta normal, abre PowerShell dentro de la carpeta `FaMa-cliente`:

```powershell
cd "ruta a FaMa-cliente"
```

Opcional, pero recomendable: comprueba que el equipo permite instalar y conectarse a la API.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\TareaPr\publicar.ps1 -ApiUrl "https://tu-direccion-real"
```

Instala el cliente:

```powershell
.\instalar.cmd
```

Debe terminar con `Instalado y en ejecucion`. Desde ese momento el cliente corre en segundo plano y arranca solo cada vez que ese usuario inicia sesión.

> Hay que ejecutarlo desde la carpeta del paquete (`FaMa-cliente`), no desde el código fuente.

## Comprobar que funciona

Mira el final del log. Deben aparecer líneas que empiezan con `Lectura`:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\FaMa\cliente\logs" |
    Sort-Object LastWriteTime | Select-Object -Last 1 |
    Get-Content -Tail 30
```

Espera un minuto y confirma en la API o en el dashboard que el PC aparece y que llegan reportes.

Por último, cierra sesión, vuelve a entrar y comprueba que el cliente arrancó solo. Esta es la prueba que decide si la instalación sirve:

```powershell
Get-Process FaMaClientMonitor
```

## Manejar el cliente

Ver si está corriendo:

```powershell
Get-Process FaMaClientMonitor
```

Detenerlo solo por ahora (vuelve a arrancar la próxima vez que se inicie sesión):

```powershell
Stop-Process -Name FaMaClientMonitor
```

Iniciarlo a mano: doble clic en `lanzar-oculto.vbs`, dentro de `%LOCALAPPDATA%\FaMa\cliente`. No cambia el arranque automático.


Pausar el cliente sin desinstalarlo. Lo detiene y quita el arranque automático, así que no vuelve a arrancar hasta que lo reanudes. No borra nada (registro del PC, cola de lecturas y logs quedan intactos). Mientras está en pausa no se toman lecturas y en la API el PC se ve inactivo:

```powershell
.TareasPr\pausar.cmd
```

Reanudarlo (vuelve el arranque automático y se inicia en el momento):

```powershell
.TareasPr\reanudar.cmd
```

`pausar.cmd` y `reanudar.cmd` también quedan instalados en `%LOCALAPPDATA%\FaMa\cliente`, así que se pueden usar sin el paquete.

Cambiar la dirección de la API en un PC (desde la carpeta del paquete; reinicia el cliente y no se pierden datos):

```powershell
.\instalar.cmd -ApiUrl https://nueva-direccion
```

Actualizar a una versión nueva: genera el paquete otra vez (paso 1), cópialo al PC y ejecuta `.\instalar.cmd` de nuevo.

Quitar el cliente del PC:

```powershell
.\desinstalar.cmd
```

Con `-ConservarDatos` no borra la carpeta, para dejar la cola de lecturas y los logs:

```powershell
.\desinstalar.cmd -ConservarDatos
```

Ver qué lee el cliente en este equipo. No envía nada y se puede usar con el cliente corriendo. Cambia `--disco` por `--identidad` (nombre, IP, MAC y UUID) o `--sensores`:

```powershell
& "$env:LOCALAPPDATA\FaMa\cliente\FaMaClientMonitor.exe" --disco
```

Si la API se cae, el cliente no pierde datos: guarda las lecturas en una cola local y las envía cuando la API vuelve.

## Configuración

Normalmente basta con `-ApiUrl` al instalar. Para ajustar otros valores en un PC concreto, crea el archivo `appsettings.Production.json` en `%LOCALAPPDATA%\FaMa\cliente` (las actualizaciones no lo borran) y reinicia el cliente:

```json
{
  "FaMa": {
    "IntervaloLecturaSegundos": 10,
    "IntervaloEnvioSegundos": 60
  }
}
```

- `ApiUrl`: dirección de la API (por defecto `http://localhost:5000`).
- `IntervaloLecturaSegundos`: cada cuánto se leen los sensores (por defecto 10, mínimo 5).
- `IntervaloEnvioSegundos`: cada cuánto se envía lo acumulado (por defecto 60, mínimo 5).
- `JitterEnvioSegundos`: variación al azar, en segundos (±), que se aplica a cada espera del envío para que varios PC no envíen a la vez (por defecto 5; con 0 se desactiva).
- `TamanoLote`: lecturas por envío (por defecto 100; la API acepta hasta 500).
- `MostrarJson`: escribe en el log el JSON de cada envío. Conviene ponerlo en `false` en los PC ya desplegados.

## Solución rápida de problemas

- `No se encuentra FaMaClientMonitor.exe`: estás ejecutando `instalar` desde el código fuente. Genera el paquete con `publicar.ps1` y ejecútalo desde `dist\FaMa-cliente`.
- Dice `Instalado` pero el proceso no corre: AppLocker o el antivirus pueden estar bloqueando el `.exe`, o `wscript` está deshabilitado. Prueba `FaMaClientMonitor.exe --disco`; si Windows lo bloquea, es una política del equipo.
- Corre, pero no llegan datos: mira el log. Si hay errores de conexión, la dirección de la API es incorrecta o la API está apagada; reinstala con `-ApiUrl`.
- No arranca al iniciar sesión: una política del equipo ignora el arranque automático. Ejecuta `probar-entorno.ps1` para ver cuál.
- `La ejecución de scripts está deshabilitada`: usa `.\instalar.cmd`. Si una política de grupo lo impide, no hay forma de evitarlo sin Administrador.
- Disco con `0.0 ms` en la primera lectura: es normal, necesita dos lecturas para calcularlo.
- Temperatura en 0 o casi constante: sin Administrador solo se dispone de la zona térmica de Windows, que en algunos equipos no existe.
- Al abrirlo dice que ya está corriendo: solo se permite una copia por equipo.
- Para registrar el PC de nuevo en la API: detén el cliente, borra `enrollment.dat` de `%LOCALAPPDATA%\FaMa\cliente` e inícialo otra vez.

## Lo que hay que saber

- Solo mide mientras hay una sesión de Windows abierta. Al cerrar sesión, apagar o suspender, el cliente se detiene. Un PC encendido sin nadie dentro no reporta, y en la API se ve igual que uno con problemas.
- Si alguien lo cierra desde el Administrador de tareas, no vuelve hasta el próximo inicio de sesión.
- Cada perfil de usuario tiene su propia instalación y su propio registro en la API.
- Si el perfil se borra al cerrar sesión, también se pierde la instalación. `probar-entorno.ps1` lo comprueba.

## Para quien desarrolle

- Los scripts de instalación y mantenimiento (`publicar`, `instalar`, `desinstalar`, `pausar`, `reanudar`, `probar-entorno`) están en `TareaPr`. Al generar el paquete se copian a su raíz, y desde ahí se usan en cada PC.
- Todo debe funcionar con una cuenta normal. No se usan servicios, tareas como SYSTEM, drivers, SMART ni WMI de `root\wmi`, porque piden Administrador.
- Ejecutar en desarrollo: `dotnet run` (monitoreo continuo) o `dotnet run -- --disco`, `--identidad`, `--sensores` (diagnósticos).
- Una lectura que falla nunca debe tumbar el programa: devuelve un valor neutro y lo registra en el log.
- Si cambias lo que se envía (`StatusReportDto` en `Api\ApiClient.cs`), cambia también el DTO y la migración de la API. Mientras la API no tenga el campo, lo ignora sin avisar.
- Para agregar una métrica: léela en `Monitors`, agrégala al DTO, a la tabla `pendientes` de `Data\SqliteDataLogger.cs` (con su `ALTER TABLE` para las bases existentes) y a `MonitorWorker.cs`, y después a la API.
- Datos locales junto al `.exe`: `monitoreo.db`, `enrollment.dat` y `logs`. Ninguno se sube a git.