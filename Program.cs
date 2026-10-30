using System.Text.Json;
using FaMaClientMonitor;
using FaMaClientMonitor.Logging;
using FaMaClientMonitor.Monitors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Modo diagnóstico: `dotnet run -- --sensores` lista los sensores que ve la
// librería (con la terminal abierta como Administrador) y termina. No arranca
// el Host: es una utilidad de consola aparte, no parte del monitoreo continuo.
if (args.Contains("--sensores"))
{
    WindowsStatusMonitor.ImprimirDiagnostico();
    WindowsStatusMonitor.Cerrar();
    return;
}

// Modo disco: `dotnet run -- --disco` muestra lo mismo que el Administrador de
// tareas (Rendimiento > Disco) con una cuenta normal, sin enviar nada, y termina.
if (args.Contains("--disco"))
{
    DiskMonitor.Info? info = DiskMonitor.ObtenerInfo();

    if (info is null)
    {
        Console.WriteLine("No se pudo leer el disco del sistema por WMI en este equipo.");
        return;
    }

    static string Gb(ulong bytes) => $"{bytes / 1_000_000_000.0:F0} GB";

    Console.WriteLine($"Disco del sistema ({info.Instancia})");
    Console.WriteLine($"  Modelo              : {info.Modelo}");
    Console.WriteLine($"  Tipo                : {info.Tipo}");
    Console.WriteLine($"  Capacidad           : {Gb(info.CapacidadBytes)}");
    Console.WriteLine($"  Con formato         : {Gb(info.ConFormatoBytes)}");
    Console.WriteLine($"  Archivo paginación  : {(info.ArchivoPaginacion ? "Sí" : "No")}");
    Console.WriteLine();

    DiskMonitor.Leer();   // muestra base: el tiempo de respuesta necesita dos lecturas
    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(1000);
        DiskMonitor.Lectura d = DiskMonitor.Leer();
        Console.WriteLine(
            $"  Actividad {d.ActividadPct,3:F0} %   Respuesta {d.RespuestaMs,5:F1} ms");
    }
    return;
}

// Modo identidad: `dotnet run -- --identidad` muestra los datos con los que
// este PC se enrolaría en la API (nombre, IP, MAC y UUID de la BIOS), sin
// enviar nada, y termina.
if (args.Contains("--identidad"))
{
    ClientIdentity.Datos identidad = ClientIdentity.Obtener();

    Console.WriteLine("Datos de identidad de este equipo (lo que se enviaría al enrolarse):");
    Console.WriteLine($"  HostName   : {identidad.HostName}");
    Console.WriteLine($"  IpAddress  : {(identidad.IpAddress.Length > 0 ? identidad.IpAddress : "(no detectada)")}");
    Console.WriteLine($"  MacAddress : {(identidad.MacAddress.Length > 0 ? identidad.MacAddress : "(no detectada)")}");
    Console.WriteLine($"  Uuid       : {(identidad.Uuid.Length > 0 ? identidad.Uuid : "(no disponible)")}");
    Console.WriteLine();

    if (identidad.Uuid.Length == 0)
    {
        Console.WriteLine("AVISO: no hay un UUID de BIOS válido (no se pudo leer o la placa trae un valor de relleno).");
    }
    if (identidad.IpAddress.Length == 0 || identidad.MacAddress.Length == 0)
    {
        Console.WriteLine("AVISO: no se detectó una interfaz de red activa; IP y MAC irían vacías.");
    }

    Console.WriteLine("JSON equivalente:");
    Console.WriteLine(JsonSerializer.Serialize(
        identidad, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return;
}

// Una sola copia por equipo. Este cliente NO es un servicio de Windows (instalar
// uno exige Administrador): se lanza al iniciar la sesión del usuario, y en un
// PC compartido dos sesiones abiertas a la vez, o un doble clic, arrancarían dos
// copias que enviarían lecturas duplicadas. El mutex es "Global\" para verse
// entre sesiones; si lo creó otra cuenta, abrirlo da UnauthorizedAccessException,
// que también significa "ya hay una copia corriendo".
// Los modos --sensores, --disco e --identidad (arriba) no pasan por aquí, así
// que se pueden usar con el cliente ya en marcha.
bool esLaPrimeraCopia;
Mutex? copiaUnica = null;
try
{
    copiaUnica = new Mutex(initiallyOwned: false, @"Global\FaMaClientMonitor", out esLaPrimeraCopia);
}
catch (UnauthorizedAccessException)
{
    esLaPrimeraCopia = false;
}

if (!esLaPrimeraCopia)
{
    Console.WriteLine("FaMa Client Monitor ya está corriendo en este equipo; esta copia se cierra.");
    return;
}

// Monitoreo continuo: se arma como un Host genérico de .NET, que se encarga
// de arrancar el Worker, de manejar Ctrl+C con cancelación ordenada, y de la
// inyección de ILogger.
//
// El Host agrega por defecto un logger de consola (mismo comportamiento que
// antes al correr `dotnet run` a mano); AddFile suma un archivo de log en
// disco, para poder revisar qué pasó cuando esto corra oculto (lanzado al
// iniciar sesión, sin ventana) y no haya ninguna consola donde mirar.
//
// ContentRootPath se fija a la carpeta del .exe a propósito: por defecto el Host
// busca appsettings.json en la carpeta DESDE DONDE se lanzó el programa, y un
// arranque automático puede lanzarlo con otra carpeta de trabajo (por ejemplo
// C:\Windows\System32), así que la configuración se ignoraría en silencio.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Compatibilidad: las variables de entorno que ya se venían usando
// (FAMA_API_URL, FAMA_PC_ID) siguen funcionando y tienen prioridad sobre el JSON.
string? urlEntorno = Environment.GetEnvironmentVariable("FAMA_API_URL");
if (!string.IsNullOrWhiteSpace(urlEntorno)) builder.Configuration["FaMa:ApiUrl"] = urlEntorno;

string? pcIdEntorno = Environment.GetEnvironmentVariable("FAMA_PC_ID");
if (!string.IsNullOrWhiteSpace(pcIdEntorno)) builder.Configuration["FaMa:PcIdManual"] = pcIdEntorno;

builder.Services.Configure<FaMaOptions>(builder.Configuration.GetSection(FaMaOptions.Seccion));

// Carpeta de datos (cola local, registro del equipo y logs). Sin indicar nada es la
// carpeta del .exe; el instalador de servicio la manda a ProgramData con
// --FaMa:DatosDir=... Debe fijarse ANTES de abrir el log o leer el enrolamiento.
Rutas.Inicializar(builder.Configuration["FaMa:DatosDir"]);

// Servicio de Windows: cuando Windows lo arranca como servicio, el Host obedece las
// órdenes de iniciar y detener del Administrador de servicios (y no las señales de
// consola). Si se ejecuta a mano (dotnet run, doble clic) no cambia nada: sigue
// siendo una aplicación de consola normal. Registrar el servicio exige
// Administrador; ver InstalacionAdmin\README.md.
builder.Services.AddWindowsService(opciones => opciones.ServiceName = "FaMaClientMonitor");

builder.Logging.AddFile(Path.Combine(Rutas.Datos, "logs"));

builder.Services.AddHostedService<MonitorWorker>();

using IHost host = builder.Build();
await host.RunAsync();

// El mutex debe seguir vivo mientras el programa corre; se libera al terminar.
GC.KeepAlive(copiaUnica);
