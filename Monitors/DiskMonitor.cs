using System.Management;

namespace FaMaClientMonitor.Monitors;

// Métricas del disco del sistema, las mismas que muestra el Administrador de
// tareas en Rendimiento > Disco. Todo se lee por WMI (root\CIMV2) y NO requiere
// Administrador. Los nombres de las clases WMI son siempre en inglés, así que
// funciona igual en Windows en español (los contadores PDH "normales" no).
//
//   Tiempo de actividad      = 100 - PercentIdleTime
//   Tiempo promedio de resp. = AvgDisksecPerTransfer (se calcula con la clase
//                              "Raw", porque la "Formatted" lo trunca a entero
//                              y siempre daría 0)
public static class DiskMonitor
{
    // Una lectura de disco lista para enviar.
    public readonly record struct Lectura(float ActividadPct, float RespuestaMs);

    // Datos que no cambian (Modelo, tipo, capacidad...). Para mostrar una vez.
    public sealed record Info(
        string Instancia, string Modelo, string Tipo,
        ulong CapacidadBytes, ulong ConFormatoBytes, bool ArchivoPaginacion);

    // Instancia WMI del disco del sistema, p. ej. "0 C:". Se busca una vez.
    private static string? _instancia;

    // Muestra anterior del contador de latencia (es acumulativo: la respuesta
    // se calcula con la diferencia entre dos lecturas, por eso se guarda).
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

    // Lee actividad y tiempo de respuesta del disco del
    // sistema. Si algo falla devuelve ceros: una métrica de disco que no se
    // pudo leer nunca debe tumbar el resto del monitoreo.
    // La PRIMERA lectura devuelve RespuestaMs = 0 (necesita dos muestras).
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

    // Tiempo promedio de respuesta en ms entre la lectura anterior y esta:
    // (Δ latencia acumulada / frecuencia del reloj) / Δ operaciones * 1000.
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

    // Datos estáticos del disco del sistema (modo --disco). Cada campo que no
    // se pueda leer queda con un valor neutro en lugar de lanzar excepción.
    public static Info? ObtenerInfo()
    {
        try
        {
            _instancia ??= BuscarInstancia();
            if (_instancia is null) return null;

            string letra = LetraSistema();
            string indice = _instancia.Split(' ')[0];   // "0 C:" -> "0"

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

    // "SSD (NVMe)", "HDD (SATA)"... MSFT_PhysicalDisk es de solo lectura y no
    // pide Administrador. MediaType: 3 = HDD, 4 = SSD. BusType: 17 = NVMe, 11 = SATA.
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
