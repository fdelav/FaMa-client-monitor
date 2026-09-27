using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FaMaClientMonitor.Monitors;

// Detecta la dirección MAC e IPv4 "principal" del equipo.
//
// Un PC suele tener varios adaptadores (Ethernet, Wi-Fi, VPN, VMware,
// Hyper-V, Docker...). Se elige el que probablemente usa el equipo para
// salir a la red: activo, con IPv4 válida y, de preferencia, con puerta de
// enlace y que no sea un adaptador virtual.
public static class NetworkInfo
{
    // MacAddress con formato AA:BB:CC:DD:EE:FF. Ambos vienen vacíos ("")
    // si no se detecta ninguna interfaz activa.
    public record Datos(string MacAddress, string IpAddress);

    private static readonly string[] _marcasVirtuales =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "vbox", "docker", "wsl", "tap-", "pseudo",
    ];

    public static Datos Obtener()
    {
        try
        {
            var candidata = NetworkInterface.GetAllNetworkInterfaces()
                .Where(EsUtil)
                .Select(ni => (Interfaz: ni, Ip: PrimeraIpv4(ni)))
                .Where(x => x.Ip is not null)
                .OrderBy(x => TieneGateway(x.Interfaz) ? 0 : 1) // primero las que tienen salida a la red
                .ThenBy(x => EsVirtual(x.Interfaz) ? 1 : 0)      // y de ésas, las físicas antes que las virtuales
                .FirstOrDefault();

            if (candidata.Interfaz is null)
            {
                return new Datos("", "");
            }

            byte[] bytesMac = candidata.Interfaz.GetPhysicalAddress().GetAddressBytes();
            string mac = string.Join(":", bytesMac.Select(b => b.ToString("X2")));

            return new Datos(mac, candidata.Ip!.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Red] No se pudo leer la información de red: {ex.Message}");
            return new Datos("", "");
        }
    }

    // Activa, que no sea loopback ni túnel, y con dirección MAC real.
    private static bool EsUtil(NetworkInterface ni)
        => ni.OperationalStatus == OperationalStatus.Up
           && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
           && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel
           && ni.GetPhysicalAddress().GetAddressBytes().Length > 0;

    // Primera IPv4 que no sea link-local (169.254.x.x, la que Windows se
    // autoasigna cuando no logra conseguir una IP de verdad).
    private static IPAddress? PrimeraIpv4(NetworkInterface ni)
        => ni.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !EsLinkLocal(ip));

    private static bool EsLinkLocal(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool TieneGateway(NetworkInterface ni)
        => ni.GetIPProperties().GatewayAddresses
            .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

    private static bool EsVirtual(NetworkInterface ni)
    {
        string texto = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        return _marcasVirtuales.Any(marca => texto.Contains(marca));
    }
}
