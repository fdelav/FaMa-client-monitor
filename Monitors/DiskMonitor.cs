using System.Management;

namespace FaMaClientMonitor.Monitors;

public static class DiskMonitor
{
    public readonly record struct Lectura(float ActividadPct, float RespuestaMs);

    public sealed record Info(
        string Instancia, string Modelo, string Tipo,
        ulong CapacidadBytes, ulong ConFormatoBytes, bool ArchivoPaginacion);

    private static string? _instancia;

    private static (ulong Latencia, ulong Base)? _previo;

    private static string LetraSistema()
        => Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

    private static string? BuscarInstancia()
    {
        string letra = LetraSistema();

        using var buscador = new ManagementObjectSearcher(
            @"root\CIMV2",
            "SELECT Name FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name <> '_Total'");

        foreach (ManagementBaseObject objeto in buscador.Get())
        {
            using (objeto)
            {
                if (objeto["Name"] is string nombre
                    && nombre.Contains(letra, StringComparison.OrdinalIgnoreCase))
                {
                    return nombre;
                }
            }
        }

        return null;
    }

    public static Lectura Leer()
    {
        try
        {
            _instancia ??= BuscarInstancia();
            if (_instancia is null) return default;

            string filtro = $"Name = '{_instancia}'";
            float actividad = 0f;

            using (var buscador = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT PercentIdleTime " +
                $"FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE {filtro}"))
            {
                foreach (ManagementBaseObject objeto in buscador.Get())
                {
                    using (objeto)
                    {
                        actividad = Math.Clamp(100f - Convert.ToSingle(objeto["PercentIdleTime"]), 0f, 100f);
                    }
                    break;
                }
            }

            return new Lectura(actividad, LeerRespuestaMs(filtro));
        }
        catch (ManagementException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static float LeerRespuestaMs(string filtro)
    {
        using var buscador = new ManagementObjectSearcher(
            @"root\CIMV2",
            "SELECT AvgDisksecPerTransfer, AvgDisksecPerTransfer_Base, Frequency_PerfTime " +
            $"FROM Win32_PerfRawData_PerfDisk_PhysicalDisk WHERE {filtro}");

        foreach (ManagementBaseObject objeto in buscador.Get())
        {
            using (objeto)
            {
                ulong latencia = Convert.ToUInt64(objeto["AvgDisksecPerTransfer"]);
                ulong operaciones = Convert.ToUInt64(objeto["AvgDisksecPerTransfer_Base"]);
                ulong frecuencia = Convert.ToUInt64(objeto["Frequency_PerfTime"]);

                float ms = 0f;

                if (_previo is { } previo
                    && latencia >= previo.Latencia
                    && operaciones > previo.Base
                    && frecuencia > 0)
                {
                    double segundos = (latencia - previo.Latencia) / (double)frecuencia;
                    ms = (float)(segundos / (operaciones - previo.Base) * 1000.0);
                }

                _previo = (latencia, operaciones);
                return ms;
            }
        }

        return 0f;
    }

    public static Info? ObtenerInfo()
    {
        try
        {
            _instancia ??= BuscarInstancia();
            if (_instancia is null) return null;

            string letra = LetraSistema();
            string indice = _instancia.Split(' ')[0];

            string modelo = "(desconocido)";
            ulong capacidad = 0;

            using (var buscador = new ManagementObjectSearcher(
                @"root\CIMV2", $"SELECT Model, Size FROM Win32_DiskDrive WHERE Index = {indice}"))
            {
                foreach (ManagementBaseObject objeto in buscador.Get())
                {
                    using (objeto)
                    {
                        modelo = (objeto["Model"] as string)?.Trim() ?? modelo;
                        if (objeto["Size"] is not null) capacidad = Convert.ToUInt64(objeto["Size"]);
                    }
                    break;
                }
            }

            string tipo = LeerTipo(indice);

            ulong conFormato = 0;
            try { conFormato = (ulong)new DriveInfo(letra + "\\").TotalSize; }
            catch (IOException) { }

            bool paginacion = false;
            using (var buscador = new ManagementObjectSearcher(
                @"root\CIMV2", "SELECT Name FROM Win32_PageFileUsage"))
            {
                foreach (ManagementBaseObject objeto in buscador.Get())
                {
                    using (objeto)
                    {
                        if (objeto["Name"] is string ruta
                            && ruta.StartsWith(letra, StringComparison.OrdinalIgnoreCase))
                        {
                            paginacion = true;
                        }
                    }
                }
            }

            return new Info(_instancia, modelo, tipo, capacidad, conFormato, paginacion);
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string LeerTipo(string indice)
    {
        try
        {
            using var buscador = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{indice}'");

            foreach (ManagementBaseObject objeto in buscador.Get())
            {
                using (objeto)
                {
                    string medio = Convert.ToInt32(objeto["MediaType"]) switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM",
                        _ => "Disco",
                    };

                    string bus = Convert.ToInt32(objeto["BusType"]) switch
                    {
                        17 => "NVMe",
                        11 => "SATA",
                        7 => "USB",
                        _ => "",
                    };

                    return bus.Length > 0 ? $"{medio} ({bus})" : medio;
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        return "(desconocido)";
    }
}
