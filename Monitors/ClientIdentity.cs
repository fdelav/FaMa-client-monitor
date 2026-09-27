using System.Management;

namespace FaMaClientMonitor.Monitors;

// Reúne los datos con los que este PC se identifica ante la API.
// Los nombres coinciden con EnrollClientComputerDto de la API
// (HostName, IpAddress, MacAddress, Uuid); en el JSON viajarían en camelCase.
public static class ClientIdentity
{
    public record Datos(string HostName, string IpAddress, string MacAddress, string Uuid);

    // Algunas placas base traen un UUID de BIOS de relleno, idéntico en muchos
    // equipos, que no sirve para distinguir un PC de otro. Se descartan.
    private static readonly HashSet<string> _uuidsDeRelleno = new(StringComparer.OrdinalIgnoreCase)
    {
        "00000000-0000-0000-0000-000000000000",
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
        "03000200-0400-0500-0006-000700080009",
    };

    public static Datos Obtener()
    {
        NetworkInfo.Datos red = NetworkInfo.Obtener();
        return new Datos(Environment.MachineName, red.IpAddress, red.MacAddress, ObtenerUuidBios());
    }

    // UUID del sistema según el SMBIOS/BIOS (Win32_ComputerSystemProduct).
    // Devuelve "" si no se puede leer o si es un valor de relleno no válido.
    public static string ObtenerUuidBios()
    {
        if (!OperatingSystem.IsWindows()) return "";

        try
        {
            using var buscador = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");

            foreach (ManagementBaseObject objeto in buscador.Get())
            {
                using (objeto)
                {
                    string uuid = objeto["UUID"]?.ToString()?.Trim() ?? "";
                    return EsUuidUtil(uuid) ? uuid : "";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Identidad] No se pudo leer el UUID de la BIOS: {ex.Message}");
        }

        return "";
    }

    private static bool EsUuidUtil(string uuid)
        => Guid.TryParse(uuid, out _) && !_uuidsDeRelleno.Contains(uuid);
}
