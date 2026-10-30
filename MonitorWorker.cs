using System.Text.Json;
using FaMaClientMonitor.Api;
using FaMaClientMonitor.Data;
using FaMaClientMonitor.Monitors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaMaClientMonitor;

public sealed class MonitorWorker(ILogger<MonitorWorker> logger, IOptions<FaMaOptions> opciones) : BackgroundService
{
    private const int TamanoLoteMaximo = 500;
    private const int IntervaloMinimoSegundos = 5;

    private readonly FaMaOptions _config = opciones.Value;
    private int _tamanoLote;
    private int _intervaloLecturaSegundos;
    private int _intervaloEnvioSegundos;
    private int _jitterEnvioSegundos;

    private int _pcId;
    private Guid? _apiKey;
    private int _pcIdManual;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string rutaBaseDeDatos = Path.Combine(Rutas.Datos, "monitoreo.db");

        string urlApi = (_config.ApiUrl ?? string.Empty).Trim().TrimEnd('/');

        if (!Uri.TryCreate(urlApi, UriKind.Absolute, out Uri? uriApi)
            || (uriApi.Scheme != Uri.UriSchemeHttp && uriApi.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"FaMa:ApiUrl no es una URL http/https válida: \"{_config.ApiUrl}\". Revisa appsettings.json.");
        }

        _pcIdManual = _config.PcIdManual;

        _intervaloLecturaSegundos = Math.Max(_config.IntervaloLecturaSegundos, IntervaloMinimoSegundos);
        _intervaloEnvioSegundos = Math.Max(_config.IntervaloEnvioSegundos, IntervaloMinimoSegundos);
        _tamanoLote = Math.Clamp(_config.TamanoLote, 1, TamanoLoteMaximo);

        _jitterEnvioSegundos = Math.Clamp(_config.JitterEnvioSegundos, 0, _intervaloEnvioSegundos / 2);

        if (_intervaloLecturaSegundos != _config.IntervaloLecturaSegundos
            || _intervaloEnvioSegundos != _config.IntervaloEnvioSegundos
            || _jitterEnvioSegundos != _config.JitterEnvioSegundos
            || _tamanoLote != _config.TamanoLote)
        {
            logger.LogWarning(
                "Configuración ajustada a los límites permitidos: IntervaloLecturaSegundos={Lectura}, IntervaloEnvioSegundos={Envio} (mínimo {Min} s), JitterEnvioSegundos={Jitter} (0 a la mitad del envío), TamanoLote={Lote} (1 a {Max}).",
                _intervaloLecturaSegundos, _intervaloEnvioSegundos, IntervaloMinimoSegundos, _jitterEnvioSegundos, _tamanoLote, TamanoLoteMaximo);
        }

        ApiClient.Registrar = mensaje => logger.LogWarning("{Mensaje}", mensaje);

        await AsegurarEnrolamientoAsync(urlApi);

        logger.LogInformation("FaMa Client Monitor — registrando datos reales de hardware");
        logger.LogInformation("PC Id: {PcId}", _pcId);
        logger.LogInformation(
            "Lectura cada {Lectura} s; envío en lotes cada {Envio} s ± {Jitter} s (hasta {Lote} lecturas por petición) a: {UrlApi}",
            _intervaloLecturaSegundos, _intervaloEnvioSegundos, _jitterEnvioSegundos, _tamanoLote, urlApi);
        logger.LogInformation("Cola local (lecturas por enviar) en: {Ruta}", rutaBaseDeDatos);

        if (!WindowsStatusMonitor.EsAdministrador())
        {
            logger.LogInformation("El programa no se ejecuta como Administrador (es lo esperado en los equipos institucionales): la temperatura real del CPU no estará disponible; se intentará la zona térmica del sistema.");
        }

        Task bucleDeEnvio = BucleDeEnvioAsync(urlApi, rutaBaseDeDatos, stoppingToken);
        await BucleDeLecturaAsync(rutaBaseDeDatos, stoppingToken);
        await bucleDeEnvio;

        WindowsStatusMonitor.Cerrar();
        logger.LogInformation("Monitoreo detenido.");
    }

    private async Task BucleDeLecturaAsync(string rutaBaseDeDatos, CancellationToken stoppingToken)
    {
        bool avisoTemperaturaMostrado = false;

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(_intervaloLecturaSegundos));

        do
        {
            try
            {
                avisoTemperaturaMostrado = TomarLectura(rutaBaseDeDatos, avisoTemperaturaMostrado);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error inesperado al leer los sensores; se continúa con la siguiente lectura.");
            }
        }
        while (await EsperarSiguienteAsync(temporizador, stoppingToken));
    }

    private async Task BucleDeEnvioAsync(string urlApi, string rutaBaseDeDatos, CancellationToken stoppingToken)
    {
        if (!await EsperarAsync(TimeSpan.FromMilliseconds(Random.Shared.Next(0, _jitterEnvioSegundos * 1000 + 1)), stoppingToken))
        {
            return;
        }

        do
        {
            try
            {
                await EnviarPendientesAsync(urlApi, rutaBaseDeDatos);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error inesperado al enviar lecturas; se reintentará en el próximo envío.");
            }
        }
        while (await EsperarAsync(SiguienteEsperaDeEnvio(), stoppingToken));
    }

    private TimeSpan SiguienteEsperaDeEnvio()
    {
        int variacionMs = Random.Shared.Next(-_jitterEnvioSegundos * 1000, _jitterEnvioSegundos * 1000 + 1);
        double segundos = Math.Max(IntervaloMinimoSegundos, _intervaloEnvioSegundos + variacionMs / 1000.0);
        return TimeSpan.FromSeconds(segundos);
    }

    private static async Task<bool> EsperarAsync(TimeSpan espera, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(espera, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> EsperarSiguienteAsync(PeriodicTimer temporizador, CancellationToken stoppingToken)
    {
        try
        {
            return await temporizador.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool TomarLectura(string rutaBaseDeDatos, bool avisoTemperaturaMostrado)
    {
        DateTime momentoLectura = DateTime.UtcNow;

        WindowsStatusMonitor.Actualizar();

        float cpu = Limpiar(WindowsStatusMonitor.GetCpuUsage(actualizar: false));
        float ram = Limpiar(WindowsStatusMonitor.GetRamUsage(actualizar: false));
        float gpu = Limpiar(WindowsStatusMonitor.GetGpuUsage(actualizar: false));
        float temperatura = Limpiar(WindowsStatusMonitor.GetCpuTemperature(actualizar: false));

        if (temperatura <= 0f && !avisoTemperaturaMostrado)
        {
            logger.LogWarning(WindowsStatusMonitor.EsAdministrador()
                ? "No hay lectura válida de temperatura en ninguna fuente. Puede faltar el driver PawnIO (winget install PawnIO.PawnIO). Ejecuta con --sensores para ver el detalle."
                : "Sin Administrador/SYSTEM no hay temperatura real del CPU y este equipo no expone una zona térmica legible: se envía como 0. Ejecuta con --sensores para ver el detalle.");
            avisoTemperaturaMostrado = true;
        }
        else if (temperatura > 0f)
        {
            avisoTemperaturaMostrado = false;
        }

        DiskMonitor.Lectura disco = DiskMonitor.Leer();

        SqliteDataLogger.GuardarPendiente(rutaBaseDeDatos, _pcId, ram, cpu, gpu, temperatura, disco, momentoLectura);

        logger.LogInformation(
            "Lectura — CPU={Cpu:F1}%  RAM={Ram:F1}%  GPU={Gpu:F1}%  Temp={Temp:F1}°C  Disco={Act:F0}% act, {Resp:F1} ms",
            cpu, ram, gpu, temperatura,
            disco.ActividadPct, disco.RespuestaMs);

        return avisoTemperaturaMostrado;
    }

    private async Task EnviarPendientesAsync(string urlApi, string rutaBaseDeDatos)
    {
        if (_apiKey is null) await AsegurarEnrolamientoAsync(urlApi);

        List<SqliteDataLogger.Pendiente> pendientes = SqliteDataLogger.ObtenerPendientes(rutaBaseDeDatos);
        if (pendientes.Count == 0) return;

        int enviadas = 0;

        foreach (var lote in pendientes.Chunk(_tamanoLote))
        {
            var reportesLote = lote
                .Select(p => new ApiClient.StatusReportDto(
                    _pcId, p.Ram, p.Cpu, p.Gpu, p.Temp, p.CreatedAt,
                    p.DiskActivity, p.DiskResponseMs))
                .ToList();

            if (_config.MostrarJson)
            {
                logger.LogInformation("JSON a enviar a {Url}/agent/statusreport/batch: {Json}",
                    urlApi, JsonSerializer.Serialize(new ApiClient.BatchStatusReportDto(reportesLote), JsonSerializerOptions.Web));
            }

            ApiClient.ResultadoEnvio resultado = await ApiClient.EnviarLoteAsync(urlApi, reportesLote, _apiKey);

            if (resultado == ApiClient.ResultadoEnvio.NoAutorizado) ReiniciarEnrolamientoPorRechazo();

            if (resultado != ApiClient.ResultadoEnvio.Enviado)
            {
                logger.LogWarning(
                    "No se pudo enviar el lote; quedan {Cantidad} lectura(s) en la cola local y se reintentará en {Segundos} s.",
                    pendientes.Count - enviadas, _intervaloEnvioSegundos);
                return;
            }

            SqliteDataLogger.EliminarPendientes(rutaBaseDeDatos, lote.Select(p => p.Id));
            enviadas += lote.Length;
        }

        logger.LogInformation("Enviado a la API — {Cantidad} lectura(s) en lote(s) de hasta {Lote}", enviadas, _tamanoLote);
    }

    private void ReiniciarEnrolamientoPorRechazo()
    {
        if (_apiKey is null) return;

        logger.LogWarning("La API rechazó el ApiKey de este equipo (401). Se descarta el enrolamiento y se volverá a enrolar.");
        EnrollmentStore.Eliminar();
        _apiKey = null;
    }

    private async Task AsegurarEnrolamientoAsync(string urlApi)
    {
        EnrollmentStore.Enrollment? enrolamiento = EnrollmentStore.Cargar();
        if (enrolamiento is not null)
        {
            _pcId = enrolamiento.ClientId;
            _apiKey = enrolamiento.ApiKey;
            return;
        }

        _pcId = _pcIdManual;
        _apiKey = null;

        logger.LogInformation("Este equipo no está enrolado en la API. Enrolando...");

        ClientIdentity.Datos identidad = ClientIdentity.Obtener();
        var solicitudEnroll = new ApiClient.EnrollRequestDto(
            identidad.HostName, identidad.IpAddress, identidad.MacAddress, identidad.Uuid);

        ApiClient.EnrollResponseDto? respuestaEnroll = await ApiClient.EnrollAsync(urlApi, solicitudEnroll);
        if (respuestaEnroll is null)
        {
            logger.LogWarning("No se pudo enrolar el equipo (API no disponible). Se reintentará en el próximo envío; mientras tanto se usa el PcId manual ({PcIdManual}).", _pcIdManual);
            return;
        }

        var respuestaConfirmacion = await ApiClient.ConfirmEnrollmentAsync(urlApi, respuestaEnroll.Id, respuestaEnroll.UsageCode);
        if (respuestaConfirmacion is null)
        {
            logger.LogWarning("No se pudo confirmar el enrolamiento. Se reintentará en el próximo envío; mientras tanto se usa el PcId manual ({PcIdManual}).", _pcIdManual);
            return;
        }

        enrolamiento = new EnrollmentStore.Enrollment(respuestaEnroll.Id, respuestaConfirmacion.ApiKey, identidad.Uuid, DateTime.UtcNow);
        EnrollmentStore.Guardar(enrolamiento);
        logger.LogInformation("Equipo enrolado correctamente. PcId asignado por la API: {PcId}", enrolamiento.ClientId);

        _pcId = enrolamiento.ClientId;
        _apiKey = enrolamiento.ApiKey;
    }

    private static float Limpiar(float valor) => float.IsFinite(valor) ? valor : 0f;
}
