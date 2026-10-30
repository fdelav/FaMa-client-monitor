using System.Text.Json;
using FaMaClientMonitor.Api;
using FaMaClientMonitor.Data;
using FaMaClientMonitor.Monitors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaMaClientMonitor;

// Toda la lógica de monitoreo vive aquí: enrolamiento, lectura de sensores,
// cola local y envío a la API. Al ser un BackgroundService, el Host de .NET se
// encarga de arrancarlo y de detenerlo con cancelación ordenada (Ctrl+C en
// consola; al cerrar la sesión de Windows el proceso termina y la cola local
// conserva lo que no alcanzó a enviarse).
//
// Hay DOS bucles independientes que se comunican por la cola local (SQLite):
//   - Lectura (cada IntervaloLecturaSegundos, 10 s por defecto): lee los
//     sensores y guarda la lectura en la cola. Nunca espera a la red.
//   - Envío (cada IntervaloEnvioSegundos, 60 s por defecto, ± JitterEnvioSegundos
//     al azar para que varios PC no envíen a la vez): manda todo lo
//     acumulado en lotes a /agent/statusreport/batch y borra de la cola lo que
//     la API aceptó. Si la API no responde, las lecturas siguen en la cola y
//     se reintenta en el siguiente envío. Si el programa se cierra de golpe,
//     lo leído hasta ese momento tampoco se pierde.
public sealed class MonitorWorker(ILogger<MonitorWorker> logger, IOptions<FaMaOptions> opciones) : BackgroundService
{
    // La API acepta entre 1 y 500 lecturas por lote.
    private const int TamanoLoteMaximo = 500;
    private const int IntervaloMinimoSegundos = 5;

    private readonly FaMaOptions _config = opciones.Value;
    private int _tamanoLote;
    private int _intervaloLecturaSegundos;
    private int _intervaloEnvioSegundos;
    private int _jitterEnvioSegundos;

    // Identidad de este equipo ante la API. Son campos (no variables locales)
    // porque pueden cambiar mientras el programa corre: si la API no estaba
    // disponible al arrancar, el enrolamiento se reintenta en cada envío; y si
    // la API rechaza el ApiKey (401), se borra y se vuelve a enrolar. Solo el
    // bucle de envío los modifica (después del arranque).
    private int _pcId;
    private Guid? _apiKey;
    private int _pcIdManual;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string rutaBaseDeDatos = Path.Combine(Rutas.Datos, "monitoreo.db");

        // La configuración viene de appsettings.json (ver FaMaOptions).
        string urlApi = (_config.ApiUrl ?? string.Empty).Trim().TrimEnd('/');

        if (!Uri.TryCreate(urlApi, UriKind.Absolute, out Uri? uriApi)
            || (uriApi.Scheme != Uri.UriSchemeHttp && uriApi.Scheme != Uri.UriSchemeHttps))
        {
            // Mejor detenerse con un mensaje claro que enviar a una dirección equivocada.
            throw new InvalidOperationException(
                $"FaMa:ApiUrl no es una URL http/https válida: \"{_config.ApiUrl}\". Revisa appsettings.json.");
        }

        _pcIdManual = _config.PcIdManual;

        _intervaloLecturaSegundos = Math.Max(_config.IntervaloLecturaSegundos, IntervaloMinimoSegundos);
        _intervaloEnvioSegundos = Math.Max(_config.IntervaloEnvioSegundos, IntervaloMinimoSegundos);
        _tamanoLote = Math.Clamp(_config.TamanoLote, 1, TamanoLoteMaximo);

        // La variación no puede pasar de la mitad del intervalo: así la espera
        // nunca se acerca a cero ni se duplica.
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

        // Cada bucle tiene su propio ritmo; el de envío puede tardar (timeouts de
        // red) sin retrasar nunca las lecturas.
        Task bucleDeEnvio = BucleDeEnvioAsync(urlApi, rutaBaseDeDatos, stoppingToken);
        await BucleDeLecturaAsync(rutaBaseDeDatos, stoppingToken);
        await bucleDeEnvio;

        WindowsStatusMonitor.Cerrar();
        logger.LogInformation("Monitoreo detenido.");
    }

    // Lee los sensores a un ritmo fijo. PeriodicTimer mantiene el intervalo sin
    // acumular desfase aunque una lectura tarde un poco.
    private async Task BucleDeLecturaAsync(string rutaBaseDeDatos, CancellationToken stoppingToken)
    {
        // Para avisar UNA vez cuando falta la temperatura (y otra si se recupera y se vuelve a perder).
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
                // Una lectura corrupta (ej. un sensor que lanzó una excepción rara)
                // no debe tumbar el programa: se registra y se sigue con la siguiente.
                logger.LogError(ex, "Error inesperado al leer los sensores; se continúa con la siguiente lectura.");
            }
        }
        while (await EsperarSiguienteAsync(temporizador, stoppingToken));
    }

    // Envía lo acumulado en la cola. La primera vez corre al arrancar (tras un
    // pequeño retraso al azar), así que si quedaron lecturas de una ejecución
    // anterior se mandan casi enseguida.
    //
    // La espera entre envíos NO es exacta: cada una dura el intervalo ± un valor
    // al azar (JitterEnvioSegundos). Con un PeriodicTimer exacto, varios PC que
    // arrancan a la vez enviarían siempre en el mismo instante; con la variación,
    // sus envíos se van repartiendo solos y la API no recibe ráfagas.
    private async Task BucleDeEnvioAsync(string urlApi, string rutaBaseDeDatos, CancellationToken stoppingToken)
    {
        // Al iniciar sesión muchos equipos arrancan casi a la vez (inicio de clase):
        // el primer envío también se desfasa.
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

    // Intervalo de envío ± una variación al azar (en milisegundos, para que el
    // reparto sea fino y dos equipos casi nunca coincidan). Nunca baja del mínimo.
    private TimeSpan SiguienteEsperaDeEnvio()
    {
        int variacionMs = Random.Shared.Next(-_jitterEnvioSegundos * 1000, _jitterEnvioSegundos * 1000 + 1);
        double segundos = Math.Max(IntervaloMinimoSegundos, _intervaloEnvioSegundos + variacionMs / 1000.0);
        return TimeSpan.FromSeconds(segundos);
    }

    // true = pasó la espera; false = se pidió detener el programa.
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

    // true = hubo otro tick; false = se pidió detener el programa.
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

    // Lee los sensores y guarda la lectura en la cola. Devuelve el nuevo valor
    // de avisoTemperaturaMostrado.
    private bool TomarLectura(string rutaBaseDeDatos, bool avisoTemperaturaMostrado)
    {
        // Se captura el momento exacto de la lectura ANTES de leer: es la hora
        // que verá la API, aunque el envío ocurra segundos o minutos después.
        DateTime momentoLectura = DateTime.UtcNow;

        // Un solo refresco del hardware por lectura; luego se consulta cada valor.
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

        // Disco del sistema (actividad y tiempo de respuesta).
        // Si no se puede leer devuelve ceros; no interrumpe el monitoreo.
        DiskMonitor.Lectura disco = DiskMonitor.Leer();

        // El PcId guardado aquí es informativo: al enviar se usa el vigente (ver EnviarPendientesAsync).
        SqliteDataLogger.GuardarPendiente(rutaBaseDeDatos, _pcId, ram, cpu, gpu, temperatura, disco, momentoLectura);

        logger.LogInformation(
            "Lectura — CPU={Cpu:F1}%  RAM={Ram:F1}%  GPU={Gpu:F1}%  Temp={Temp:F1}°C  Disco={Act:F0}% act, {Resp:F1} ms",
            cpu, ram, gpu, temperatura,
            disco.ActividadPct, disco.RespuestaMs);

        return avisoTemperaturaMostrado;
    }

    // Envía todo lo acumulado en la cola, en lotes de hasta _tamanoLote
    // lecturas. Si un lote falla se corta: lo no enviado sigue en la cola y se
    // reintenta en el siguiente envío (así una API caída cuesta un solo
    // timeout por envío y no uno por lote).
    private async Task EnviarPendientesAsync(string urlApi, string rutaBaseDeDatos)
    {
        // Sin ApiKey la API de producción rechaza todo con 401: si el
        // enrolamiento no se pudo hacer antes (API caída al arrancar, o se
        // borró por un 401), se reintenta ahora, una vez por envío.
        if (_apiKey is null) await AsegurarEnrolamientoAsync(urlApi);

        List<SqliteDataLogger.Pendiente> pendientes = SqliteDataLogger.ObtenerPendientes(rutaBaseDeDatos);
        if (pendientes.Count == 0) return;

        int enviadas = 0;

        foreach (var lote in pendientes.Chunk(_tamanoLote))
        {
            // Se usa el PcId vigente y no el guardado con cada lectura: son
            // todas de este mismo equipo, y si se guardaron antes de enrolarse
            // (PcId manual) o antes de un re-enrolamiento, el guardado ya no
            // identifica a este PC y mandarlo ensuciaría los datos de otro.
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

    // Si la API rechaza el ApiKey (401) el enrolamiento guardado ya no sirve
    // (por ejemplo, la base de datos del servidor se reinició). Se borra para
    // que el próximo envío enrole de nuevo; reintentar con la misma clave
    // solo acumularía lecturas en la cola local sin que nunca se envíen.
    private void ReiniciarEnrolamientoPorRechazo()
    {
        if (_apiKey is null) return;   // sin clave no hay nada que invalidar

        logger.LogWarning("La API rechazó el ApiKey de este equipo (401). Se descarta el enrolamiento y se volverá a enrolar.");
        EnrollmentStore.Eliminar();
        _apiKey = null;
    }

    // Enrolamiento: si este equipo no tiene un enrolamiento guardado (o quedó
    // dañado/de otra máquina), se enrola solo, sin pedir nada por consola. Es
    // un proceso de dos pasos contra la API: /clientcomputer/enroll entrega un
    // usageCode de un solo uso (válido 5 min), que se confirma de inmediato en
    // /clientcomputer/confirm_enrollment para obtener el ApiKey definitivo.
    //
    // Deja en _pcId/_apiKey el resultado. Si no se logra (API caída), queda
    // _apiKey = null y _pcId = el PcId manual; el Worker lo reintenta en el
    // siguiente envío.
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

    // Un valor NaN/Infinito no se puede serializar a JSON ni guardar en SQLite;
    // se reemplaza por 0 para que una lectura rara no tumbe el monitoreo.
    private static float Limpiar(float valor) => float.IsFinite(valor) ? valor : 0f;
}
