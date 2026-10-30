using System.Text.Json;
using FaMaClientMonitor;
using FaMaClientMonitor.Logging;
using FaMaClientMonitor.Monitors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--sensores"))
{
    WindowsStatusMonitor.ImprimirDiagnostico();
    WindowsStatusMonitor.Cerrar();
    return;
}

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

    DiskMonitor.Leer();
    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(1000);
        DiskMonitor.Lectura d = DiskMonitor.Leer();
        Console.WriteLine(
            $"  Actividad {d.ActividadPct,3:F0} %   Respuesta {d.RespuestaMs,5:F1} ms");
    }
    return;
}

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

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

string? urlEntorno = Environment.GetEnvironmentVariable("FAMA_API_URL");
if (!string.IsNullOrWhiteSpace(urlEntorno)) builder.Configuration["FaMa:ApiUrl"] = urlEntorno;

string? pcIdEntorno = Environment.GetEnvironmentVariable("FAMA_PC_ID");
if (!string.IsNullOrWhiteSpace(pcIdEntorno)) builder.Configuration["FaMa:PcIdManual"] = pcIdEntorno;

builder.Services.Configure<FaMaOptions>(builder.Configuration.GetSection(FaMaOptions.Seccion));

Rutas.Inicializar(builder.Configuration["FaMa:DatosDir"]);

builder.Services.AddWindowsService(opciones => opciones.ServiceName = "FaMaClientMonitor");

builder.Logging.AddFile(Path.Combine(Rutas.Datos, "logs"));

builder.Services.AddHostedService<MonitorWorker>();

using IHost host = builder.Build();
await host.RunAsync();

GC.KeepAlive(copiaUnica);
