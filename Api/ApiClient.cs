using System.Net.Http.Json;
using System.Text.Json;

namespace FaMaClientMonitor.Api;

// Encapsula el envío de reportes de estado a la API central (FaMa-Api).
public static class ApiClient
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    // La API serializa con camelCase (ej. "usageCode", "apiKey") y acepta el
    // nombre de propiedad sin distinguir mayúsculas al recibir JSON.
    private static readonly JsonSerializerOptions _jsonWeb = new(JsonSerializerDefaults.Web);

    // Debe coincidir con CreateStatusReportDto del lado de la API.
    // CreatedAt es el momento REAL en que se tomó la lectura (no cuando se
    // envía) — importante para que los reintentos de la cola de pendientes
    // no queden con la hora del reenvío en vez de la hora real.
    public record StatusReportDto(int PcId, float Ram, float Cpu, float Gpu, float Temp, DateTime CreatedAt);

    // Debe coincidir con BatchStatusReportDto de la API: { "statusReports": [ ... ] }
    public record BatchStatusReportDto(List<StatusReportDto> StatusReports);

    // POST /statusreport — una sola lectura. true si la API respondió 2xx.
    public static Task<bool> EnviarReporteAsync(string urlBase, StatusReportDto reporte)
        => PostAsync($"{urlBase}/statusreport", reporte);

    // POST /statusreport/batch — varias lecturas en una sola petición.
    // La API acepta entre 1 y 500 por lote y lo guarda de forma atómica
    // (o entra todo el lote o no entra nada).
    public static Task<bool> EnviarLoteAsync(string urlBase, List<StatusReportDto> reportes)
        => PostAsync($"{urlBase}/statusreport/batch", new BatchStatusReportDto(reportes));

    private static async Task<bool> PostAsync<T>(string url, T cuerpo)
    {
        try
        {
            using HttpResponseMessage respuesta = await _http.PostAsJsonAsync(url, cuerpo);

            if (respuesta.IsSuccessStatusCode)
            {
                return true;
            }

            // La API SÍ respondió, pero rechazó la petición (400, 404, 500...).
            // Se muestra el motivo para no confundirlo con "servidor caído".
            string detalle = await respuesta.Content.ReadAsStringAsync();
            Console.WriteLine(
                $"[API] POST {url} -> {(int)respuesta.StatusCode} {respuesta.ReasonPhrase}. {Recortar(detalle)}");
            return false;
        }
        catch (Exception ex)
        {
            // Sin conexión, DNS, timeout, certificado HTTPS no confiable, etc.
            Console.WriteLine($"[API] No se pudo contactar {url}: {ex.GetType().Name}: {ex.Message}");
            return false;
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
                Console.WriteLine(
                    $"[API] POST {url} -> {(int)respuesta.StatusCode} {respuesta.ReasonPhrase}. {Recortar(detalle)}");
                return null;
            }

            return await respuesta.Content.ReadFromJsonAsync<TRespuesta>(_jsonWeb);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[API] No se pudo contactar {url}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
