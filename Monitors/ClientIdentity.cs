using System.Management;

namespace FaMaClientMonitor.Monitors;

public static class ClientIdentity
{
    public record Datos(string HostName, string IpAddress, string MacAddress, string Uuid);

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
