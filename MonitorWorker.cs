using FaMaClientMonitor.Api;
using FaMaClientMonitor.Data;
using FaMaClientMonitor.Monitors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FaMaClientMonitor;

// Toda la lógica de monitoreo vive aquí: enrolamiento, lectura de sensores,
// envío a la API y cola local de reintentos. Al ser un BackgroundService, el
// Host de .NET se encarga de arrancarlo y de detenerlo con cancelación
// ordenada (Ctrl+C en consola, o la señal de parada que mande Windows si más
// adelante esto se instala como servicio o se lanza desde una tarea
// programada). Antes esto vivía suelto en Program.cs con un CancellationTokenSource manual.
public sealed class MonitorWorker(ILogger<MonitorWorker> logger) : BackgroundService
{
    private const int IntervaloSegundos = 60;
    private const int TamanoLote = 100; // la API acepta hasta 500 por lote

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string rutaBaseDeDatos = Path.Combine(AppContext.BaseDirectory, "monitoreo.db");

        // Configuración por variables de entorno (sin recompilar para cada equipo):
        //   FAMA_API_URL  ej. http://192.168.1.50:5000   (por defecto http://localhost:5000)
        //   FAMA_PC_ID    ej. 7                          (por defecto 1; solo se usa si el enrolamiento falla)
        string urlApi = (Environment.GetEnvironmentVariable("FAMA_API_URL") ?? "http://localhost:5000").TrimEnd('/');
        int pcIdManual = int.TryParse(Environment.GetEnvironmentVariable("FAMA_PC_ID"), out int pcIdConfigurado)
            ? pcIdConfigurado : 1;

        int pcId = await ResolverPcIdAsync(urlApi, pcIdManual);

        logger.LogInformation("FaMa Client Monitor — registrando datos reales de hardware");
        logger.LogInformation("PC Id: {PcId}", pcId);
        logger.LogInformation("Enviando a la API en: {UrlApi}", urlApi);
        logger.LogInformation("Respaldo local (si la API no responde) en: {Ruta}", rutaBaseDeDatos);

        if (!WindowsStatusMonitor.EsAdministrador())
        {
            logger.LogWarning("El programa NO se ejecuta como Administrador; la temperatura del CPU se enviará como 0.");
        }

        // Para avisar UNA vez cuando falta la temperatura (y otra si se recupera y se vuelve a perder).
        bool avisoTemperaturaMostrado = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                avisoTemperaturaMostrado = await EjecutarCicloAsync(urlApi, pcId, rutaBaseDeDatos, avisoTemperaturaMostrado);
            }
            catch (Exception ex)
            {
                // Un ciclo corrupto (ej. un sensor que lanzó una excepción rara)
                // no debe tumbar el servicio completo: se registra y se sigue
                // en el siguiente ciclo. Esto es más importante ahora que antes,
                // porque en segundo plano nadie ve un error y reinicia a mano.
                logger.LogError(ex, "Error inesperado en un ciclo de monitoreo; se continúa en el siguiente.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervaloSegundos), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        WindowsStatusMonitor.Cerrar();
        logger.LogInformation("Monitoreo detenido.");
    }

    // Devuelve el nuevo valor de avisoTemperaturaMostrado (los métodos async no
    // admiten parámetros ref/out, así que el estado viaja como valor de retorno).
    private async Task<bool> EjecutarCicloAsync(string urlApi, int pcId, string rutaBaseDeDatos, bool avisoTemperaturaMostrado)
    {
        // Se captura el momento exacto de la lectura ANTES de intentar
        // enviarla — así, si termina en la cola de pendientes y se reenvía
        // minutos/horas después, la API sigue recibiendo la hora real en que
        // se tomó la muestra, no la hora del reenvío.
        DateTime momentoLectura = DateTime.UtcNow;

        float cpu = Limpiar(WindowsStatusMonitor.GetCpuUsage());
        float ram = Limpiar(WindowsStatusMonitor.GetRamUsage());
        float gpu = Limpiar(WindowsStatusMonitor.GetGpuUsage());
        float temperatura = Limpiar(WindowsStatusMonitor.GetCpuTemperature());

        if (temperatura <= 0f && !avisoTemperaturaMostrado)
        {
            logger.LogWarning(WindowsStatusMonitor.EsAdministrador()
                ? "No hay lectura válida de temperatura del CPU pese a ser Administrador. Puede faltar el driver PawnIO (winget install PawnIO.PawnIO). Ejecuta con --sensores para ver qué sensores detecta."
                : "Sin permisos de Administrador la temperatura del CPU se envía como 0.");
            avisoTemperaturaMostrado = true;
        }
        else if (temperatura > 0f)
        {
            avisoTemperaturaMostrado = false;
        }

        var reporte = new ApiClient.StatusReportDto(pcId, ram, cpu, gpu, temperatura, momentoLectura);
        bool enviado = await ApiClient.EnviarReporteAsync(urlApi, reporte);

        if (enviado)
        {
            logger.LogInformation(
                "Enviado a la API — CPU={Cpu:F1}%  RAM={Ram:F1}%  GPU={Gpu:F1}%  Temp={Temp:F1}°C", cpu, ram, gpu, temperatura);
        }
        else
        {
            SqliteDataLogger.GuardarPendiente(rutaBaseDeDatos, pcId, ram, cpu, gpu, temperatura, momentoLectura);
            logger.LogWarning("No se pudo enviar — guardado localmente como pendiente");
        }

        // Solo se reintenta la cola cuando la API acaba de responder bien: si
        // el envío de arriba falló, insistir solo gastaría otro timeout.
        // Los pendientes se envían por lotes (menos peticiones) y, si un lote
        // falla, se corta y se vuelve a intentar en el próximo ciclo.
        if (!enviado) return avisoTemperaturaMostrado;

        var pendientes = SqliteDataLogger.ObtenerPendientes(rutaBaseDeDatos);
        if (pendientes.Count == 0) return avisoTemperaturaMostrado;

        logger.LogInformation("Reintentando {Cantidad} lectura(s) pendiente(s)...", pendientes.Count);

        foreach (var lote in pendientes.Chunk(TamanoLote))
        {
            var reportesLote = lote
                .Select(p => new ApiClient.StatusReportDto(p.PcId, p.Ram, p.Cpu, p.Gpu, p.Temp, p.CreatedAt))
                .ToList();

            bool loteEnviado = await ApiClient.EnviarLoteAsync(urlApi, reportesLote);
            if (!loteEnviado) break;

            SqliteDataLogger.EliminarPendientes(rutaBaseDeDatos, lote.Select(p => p.Id));
            logger.LogInformation("Lote de {Cantidad} pendiente(s) reenviado y eliminado de la cola local", lote.Length);
        }

        return avisoTemperaturaMostrado;
    }

    // Enrolamiento: si este equipo no tiene un enrolamiento guardado (o quedó
    // dañado/de otra máquina), se enrola solo, sin pedir nada por consola. Es
    // un proceso de dos pasos contra la API: /clientcomputer/enroll entrega un
    // usageCode de un solo uso (válido 5 min), que se confirma de inmediato en
    // /clientcomputer/confirm_enrollment para obtener el ApiKey definitivo.
    private async Task<int> ResolverPcIdAsync(string urlApi, int pcIdManual)
    {
        EnrollmentStore.Enrollment? enrolamiento = EnrollmentStore.Cargar();
        if (enrolamiento is not null) return enrolamiento.ClientId;

        logger.LogInformation("Este equipo no está enrolado en la API. Enrolando...");

        ClientIdentity.Datos identidad = ClientIdentity.Obtener();
        var solicitudEnroll = new ApiClient.EnrollRequestDto(
            identidad.HostName, identidad.IpAddress, identidad.MacAddress, identidad.Uuid);

        ApiClient.EnrollResponseDto? respuestaEnroll = await ApiClient.EnrollAsync(urlApi, solicitudEnroll);
        if (respuestaEnroll is null)
        {
            logger.LogWarning("No se pudo enrolar el equipo (API no disponible). Se usará el PcId manual ({PcIdManual}) mientras tanto.", pcIdManual);
            return pcIdManual;
        }

        var respuestaConfirmacion = await ApiClient.ConfirmEnrollmentAsync(urlApi, respuestaEnroll.Id, respuestaEnroll.UsageCode);
        if (respuestaConfirmacion is null)
        {
            logger.LogWarning("No se pudo confirmar el enrolamiento. Se usará el PcId manual ({PcIdManual}) mientras tanto.", pcIdManual);
            return pcIdManual;
        }

        enrolamiento = new EnrollmentStore.Enrollment(respuestaEnroll.Id, respuestaConfirmacion.ApiKey, identidad.Uuid, DateTime.UtcNow);
        EnrollmentStore.Guardar(enrolamiento);
        logger.LogInformation("Equipo enrolado correctamente. PcId asignado por la API: {PcId}", enrolamiento.ClientId);

        return enrolamiento.ClientId;
    }

    // Un valor NaN/Infinito no se puede serializar a JSON ni guardar en SQLite;
    // se reemplaza por 0 para que una lectura rara no tumbe el monitoreo.
    private static float Limpiar(float valor) => float.IsFinite(valor) ? valor : 0f;
}
