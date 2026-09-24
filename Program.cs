using FaMaClientMonitor.Api;
using FaMaClientMonitor.Data;
using FaMaClientMonitor.Monitors;

// La BD de respaldo se guarda junto al ejecutable (y no en la carpeta desde la
// que se lance el programa), así no se mezcla con archivos .db de otras versiones.
string rutaBaseDeDatos = Path.Combine(AppContext.BaseDirectory, "monitoreo.db");

// Configuración por variables de entorno (sin recompilar para cada equipo):
//   FAMA_API_URL  ej. http://192.168.1.50:5000   (por defecto http://localhost:5000)
//   FAMA_PC_ID    ej. 7                          (por defecto 1)
string urlApi = (Environment.GetEnvironmentVariable("FAMA_API_URL") ?? "http://localhost:5000").TrimEnd('/');
int pcId = int.TryParse(Environment.GetEnvironmentVariable("FAMA_PC_ID"), out int pcIdConfigurado) ? pcIdConfigurado : 1;

const int intervaloSegundos = 60;
const int tamanoLote = 100; // la API acepta hasta 500 por lote

// Un valor NaN/Infinito no se puede serializar a JSON ni guardar en SQLite;
// se reemplaza por 0 para que una lectura rara no tumbe el monitoreo.
static float Limpiar(float valor) => float.IsFinite(valor) ? valor : 0f;

Console.WriteLine("FaMa Client Monitor — registrando datos reales de hardware");
Console.WriteLine($"PC Id: {pcId}");
Console.WriteLine($"Enviando a la API en: {urlApi}");
Console.WriteLine($"Respaldo local (si la API no responde) en: {rutaBaseDeDatos}");
Console.WriteLine("Presiona Ctrl+C para detener.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.IsCancellationRequested)
    {
        // Se captura el momento exacto de la lectura ANTES de intentar
        // enviarla — así, si termina en la cola de pendientes y se
        // reenvía minutos/horas después, la API sigue recibiendo la hora
        // real en que se tomó la muestra, no la hora del reenvío.
        DateTime momentoLectura = DateTime.UtcNow;

        float cpu = Limpiar(WindowsStatusMonitor.GetCpuUsage());
        float ram = Limpiar(WindowsStatusMonitor.GetRamUsage());
        float gpu = Limpiar(WindowsStatusMonitor.GetGpuUsage());
        float temperatura = Limpiar(WindowsStatusMonitor.GetCpuTemperature());

        var reporte = new ApiClient.StatusReportDto(pcId, ram, cpu, gpu, temperatura, momentoLectura);
        bool enviado = await ApiClient.EnviarReporteAsync(urlApi, reporte);

        if (enviado)
        {
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] Enviado a la API — CPU={cpu:F1}%  RAM={ram:F1}%  GPU={gpu:F1}%  Temp={temperatura:F1}°C");
        }
        else
        {
            SqliteDataLogger.GuardarPendiente(rutaBaseDeDatos, pcId, ram, cpu, gpu, temperatura, momentoLectura);
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] No se pudo enviar — guardado localmente como pendiente");
        }

        // Solo se reintenta la cola cuando la API acaba de responder bien: si
        // el envío de arriba falló, insistir solo gastaría otro timeout.
        // Los pendientes se envían por lotes (menos peticiones) y, si un lote
        // falla, se corta y se vuelve a intentar en el próximo ciclo.
        if (enviado)
        {
            var pendientes = SqliteDataLogger.ObtenerPendientes(rutaBaseDeDatos);
            if (pendientes.Count > 0)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Reintentando {pendientes.Count} lectura(s) pendiente(s)...");

                foreach (var lote in pendientes.Chunk(tamanoLote))
                {
                    var reportesLote = lote
                        .Select(p => new ApiClient.StatusReportDto(p.PcId, p.Ram, p.Cpu, p.Gpu, p.Temp, p.CreatedAt))
                        .ToList();

                    bool loteEnviado = await ApiClient.EnviarLoteAsync(urlApi, reportesLote);
                    if (!loteEnviado)
                    {
                        break;
                    }

                    SqliteDataLogger.EliminarPendientes(rutaBaseDeDatos, lote.Select(p => p.Id));
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] Lote de {lote.Length} pendiente(s) reenviado y eliminado de la cola local");
                }
            }
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(intervaloSegundos), cts.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }
}
finally
{
    WindowsStatusMonitor.Cerrar();
    Console.WriteLine();
    Console.WriteLine("Monitoreo detenido.");
}
