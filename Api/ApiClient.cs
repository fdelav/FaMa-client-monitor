using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaMaClientMonitor.Api;

// Encapsula el envío de reportes de estado a la API central (FaMa-Api).
public static class ApiClient
{
    // En redes institucionales es habitual salir a internet por un proxy que pide
    // autenticarse (error 407). DefaultProxyCredentials usa las credenciales de la
    // sesión de Windows del usuario, igual que un navegador; si no hay proxy, o el
    // proxy no pide credenciales, no cambia nada.
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
        // Evita la página de advertencia de ngrok (plan gratuito) cuando la API
        // se expone por un túnel; a una API normal no le afecta.
        DefaultRequestHeaders = { { "ngrok-skip-browser-warning", "true" } },
    };

    // Los mensajes de error de este cliente salían solo por Console.WriteLine,
    // que oculto (lanzado al iniciar sesión, sin ventana) no lee nadie. El
    // Worker reemplaza este destino por su ILogger para que queden en el
    // archivo de log. Si nadie lo reemplaza (modo --sensores, pruebas), sigue
    // yendo a la consola como antes.
    public static Action<string> Registrar { get; set; } = Console.WriteLine;

    // Resultado de un envío de lecturas. Se distingue NoAutorizado (401) de
    // un fallo cualquiera porque se arregla distinto: reintentar no sirve,
    // hay que volver a enrolar el equipo para obtener un ApiKey válido.
    public enum ResultadoEnvio { Enviado, NoAutorizado, Fallido }

    // Nombre del encabezado que exige la API (ApiKeyAuthFilter) en /agent/*.
    private const string EncabezadoApiKey = "X-Report-Api-Key";

    // La API serializa con camelCase (ej. "usageCode", "apiKey") y acepta el
    // nombre de propiedad sin distinguir mayúsculas al recibir JSON.
    private static readonly JsonSerializerOptions _jsonWeb = new(JsonSerializerDefaults.Web);

    // Debe coincidir con CreateStatusReportDto del lado de la API.
    // CreatedAt es el momento REAL en que se tomó la lectura (no cuando se
    // envía) — importante para que los reintentos de la cola de pendientes
    // no queden con la hora del reenvío en vez de la hora real.
    public record StatusReportDto(
        int PcId, float Ram, float Cpu, float Gpu, float Temp, DateTime CreatedAt,
        float DiskActivity, float DiskResponseMs);

    // Debe coincidir con BatchStatusReportDto de la API: { "statusReports": [ ... ] }
    public record BatchStatusReportDto(List<StatusReportDto> StatusReports);
    // En Development la API acepta reportes sin ApiKey; en Production exige el
    // encabezado X-Report-Api-Key con la clave que el equipo recibió al
    // enrolarse. Si apiKey es null no se manda el encabezado.
    public static Task<ResultadoEnvio> EnviarReporteAsync(string urlBase, StatusReportDto reporte, Guid? apiKey)
        => PostReportesAsync($"{urlBase}/agent/statusreport", reporte, apiKey);

    // POST /agent/statusreport/batch — varias lecturas en una sola petición.
    // La API acepta entre 1 y 500 por lote y lo guarda de forma atómica
    // (o entra todo el lote o no entra nada).
    public static Task<ResultadoEnvio> EnviarLoteAsync(string urlBase, List<StatusReportDto> reportes, Guid? apiKey)
        => PostReportesAsync($"{urlBase}/agent/statusreport/batch", new BatchStatusReportDto(reportes), apiKey);

    private static async Task<ResultadoEnvio> PostReportesAsync<T>(string url, T cuerpo, Guid? apiKey)
    {
        try
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(cuerpo, options: _jsonWeb),
            };

            if (apiKey is Guid clave)
            {
                peticion.Headers.Add(EncabezadoApiKey, clave.ToString());
            }

            using HttpResponseMessage respuesta = await _http.SendAsync(peticion);

            if (respuesta.IsSuccessStatusCode) return ResultadoEnvio.Enviado;

            // La API SÍ respondió, pero rechazó la petición (400, 401, 404, 500...).
            // Se muestra el motivo para no confundirlo con "servidor caído".
            string detalle = await respuesta.Content.ReadAsStringAsync();
            Registrar($"[API] POST {url} -> {(int)respuesta.StatusCode} {respuesta.ReasonPhrase}. {Recortar(detalle)}");

            return respuesta.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? ResultadoEnvio.NoAutorizado
                : ResultadoEnvio.Fallido;
        }
        catch (Exception ex)
        {
            // Sin conexión, DNS, timeout, certificado HTTPS no confiable, etc.
            Registrar($"[API] No se pudo contactar {url}: {ex.GetType().Name}: {ex.Message}");
            return ResultadoEnvio.Fallido;
        }
    }

    private static string Recortar(string texto)
        => texto.Length <= 200 ? texto : texto[..200] + "…";

    // ---------------------------------------------------------------
    // Enrolamiento (POST /clientcomputer/enroll y /confirm_enrollment)
    // ---------------------------------------------------------------

    // Debe coincidir con EnrollClientComputerDto del lado de la API.
    public record EnrollRequestDto(string HostName, string IpAddress, string MacAddress, string Uuid);

    // Debe coincidir con EnrollResponseDto del lado de la API.
    public record EnrollResponseDto(int Id, string UsageCode, DateTime UsageCodeExpiration, string Message);

    // Debe coincidir con ConfirmEnrollmentDto del lado de la API.
    public record ConfirmEnrollmentRequestDto(int ClientId, string UsageCode);

    // La API responde con un objeto anónimo { Message, ApiKey }; se define
    // aquí la forma que toma una vez serializado, para poder deserializarlo.
    public record ConfirmEnrollmentResponseDto(string Message, Guid ApiKey);

    // POST /clientcomputer/enroll — registra (o actualiza) este equipo por su
    // Uuid y devuelve un usageCode de un solo uso, válido 5 minutos, con el
    // que se completa el enrolamiento en ConfirmEnrollmentAsync.
    // Devuelve null si la API no respondió o rechazó la petición.
    public static Task<EnrollResponseDto?> EnrollAsync(string urlBase, EnrollRequestDto identidad)
        => PostAndReadAsync<EnrollResponseDto>($"{urlBase}/clientcomputer/enroll", identidad);

    // POST /clientcomputer/confirm_enrollment — confirma el usageCode
    // obtenido en EnrollAsync y devuelve el ApiKey definitivo del equipo.
    // Devuelve null si el código ya expiró (5 min) o no coincide.
    public static Task<ConfirmEnrollmentResponseDto?> ConfirmEnrollmentAsync(string urlBase, int clientId, string usageCode)
        => PostAndReadAsync<ConfirmEnrollmentResponseDto>(
            $"{urlBase}/clientcomputer/confirm_enrollment", new ConfirmEnrollmentRequestDto(clientId, usageCode));

    private static async Task<TRespuesta?> PostAndReadAsync<TRespuesta>(string url, object cuerpo)
        where TRespuesta : class
    {
        try
        {
            using HttpResponseMessage respuesta = await _http.PostAsJsonAsync(url, cuerpo);

            if (!respuesta.IsSuccessStatusCode)
            {
                string detalle = await respuesta.Content.ReadAsStringAsync();
                Registrar($"[API] POST {url} -> {(int)respuesta.StatusCode} {respuesta.ReasonPhrase}. {Recortar(detalle)}");
                return null;
            }

            return await respuesta.Content.ReadFromJsonAsync<TRespuesta>(_jsonWeb);
        }
        catch (Exception ex)
        {
            Registrar($"[API] No se pudo contactar {url}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
