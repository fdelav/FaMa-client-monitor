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

// Monitoreo continuo: se arma como un Host genérico de .NET, que se encarga
// de arrancar el Worker, de manejar Ctrl+C (o la señal de parada que mande
// Windows) con cancelación ordenada, y de la inyección de ILogger.
//
// El Host agrega por defecto un logger de consola (mismo comportamiento que
// antes al correr `dotnet run` a mano); AddFile suma un archivo de log en
// disco, para poder revisar qué pasó cuando esto corra en segundo plano y no
// haya ninguna consola donde mirar.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFile(Path.Combine(AppContext.BaseDirectory, "logs"));

builder.Services.AddHostedService<MonitorWorker>();

using IHost host = builder.Build();
await host.RunAsync();
