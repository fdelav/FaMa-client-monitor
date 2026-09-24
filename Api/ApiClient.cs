using System.Net.Http.Json;

namespace FaMaClientMonitor.Api;

// Encapsula el envío de reportes de estado a la API central (FaMa-Api).
public static class ApiClient
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

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
}
