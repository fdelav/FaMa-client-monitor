using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaMaClientMonitor.Api;

public static class ApiClient
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "ngrok-skip-browser-warning", "true" } },
    };

    public static Action<string> Registrar { get; set; } = Console.WriteLine;

    public enum ResultadoEnvio { Enviado, NoAutorizado, Fallido }

    private const string EncabezadoApiKey = "X-Report-Api-Key";

    private static readonly JsonSerializerOptions _jsonWeb = new(JsonSerializerDefaults.Web);

    public record StatusReportDto(
        int PcId, float Ram, float Cpu, float Gpu, float Temp, DateTime CreatedAt,
        float DiskActivity, float DiskResponseMs);

    public record BatchStatusReportDto(List<StatusReportDto> StatusReports);
    public static Task<ResultadoEnvio> EnviarReporteAsync(string urlBase, StatusReportDto reporte, Guid? apiKey)
        => PostReportesAsync($"{urlBase}/agent/statusreport", reporte, apiKey);

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

            string detalle = await respuesta.Content.ReadAsStringAsync();
            Registrar($"[API] POST {url} -> {(int)respuesta.StatusCode} {respuesta.ReasonPhrase}. {Recortar(detalle)}");

            return respuesta.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? ResultadoEnvio.NoAutorizado
                : ResultadoEnvio.Fallido;
        }
        catch (Exception ex)
        {
            Registrar($"[API] No se pudo contactar {url}: {ex.GetType().Name}: {ex.Message}");
            return ResultadoEnvio.Fallido;
        }
    }

    private static string Recortar(string texto)
        => texto.Length <= 200 ? texto : texto[..200] + "…";

    public record EnrollRequestDto(string HostName, string IpAddress, string MacAddress, string Uuid);

    public record EnrollResponseDto(int Id, string UsageCode, DateTime UsageCodeExpiration, string Message);

    public record ConfirmEnrollmentRequestDto(int ClientId, string UsageCode);

    public record ConfirmEnrollmentResponseDto(string Message, Guid ApiKey);

    public static Task<EnrollResponseDto?> EnrollAsync(string urlBase, EnrollRequestDto identidad)
        => PostAndReadAsync<EnrollResponseDto>($"{urlBase}/clientcomputer/enroll", identidad);

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
