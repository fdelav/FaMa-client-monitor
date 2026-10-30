using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace FaMaClientMonitor.Monitors;

public static class WindowsStatusMonitor
{
    private static readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
    };

    private static bool _abierto = false;

    private static void AsegurarAbierto()
    {
        if (!_abierto)
        {
            _computer.Open();
            _abierto = true;
        }
    }

    private static void ActualizarLecturas()
    {
        AsegurarAbierto();
        foreach (IHardware hardware in _computer.Hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                subHardware.Update();
            }
        }
    }

    public static void Actualizar() => ActualizarLecturas();

    public static float GetCpuUsage(bool actualizar = true)
    {
        if (actualizar) ActualizarLecturas();

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            ISensor? total = hardware.Sensors.FirstOrDefault(
                s => s.SensorType == SensorType.Load && s.Name == "CPU Total");

            if (total?.Value is float valor) return valor;
        }

        return 0f;
    }

    public static float GetRamUsage(bool actualizar = true)
    {
        if (actualizar) ActualizarLecturas();

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Memory) continue;

            ISensor? uso = hardware.Sensors.FirstOrDefault(
                s => s.SensorType == SensorType.Load && s.Name == "Memory");

            if (uso?.Value is float valor) return valor;
        }

        return 0f;
    }

    public static float GetGpuUsage(bool actualizar = true)
    {
        if (actualizar) ActualizarLecturas();

        HardwareType[] tiposGpu =
        [
            HardwareType.GpuNvidia,
            HardwareType.GpuAmd,
            HardwareType.GpuIntel,
        ];

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (!tiposGpu.Contains(hardware.HardwareType)) continue;

            ISensor? core = hardware.Sensors.FirstOrDefault(
                s => s.SensorType == SensorType.Load && s.Name.Contains("Core"));

            if (core?.Value is float valor) return valor;
        }

        return 0f;
    }

    private const float TempMinValida = 1f;
    private const float TempMaxValida = 125f;

    private static readonly string[] _sensoresTempPreferidos =
    [
        "CPU Package",
        "Core (Tctl/Tdie)",
        "Tctl",
        "Tdie",
        "Package",
        "Core Average",
        "Core Max",
    ];

    private static bool TieneLecturaValida(ISensor sensor)
        => sensor.Value is float valor && valor >= TempMinValida && valor <= TempMaxValida;

    public static float GetCpuTemperature(bool actualizar = true)
    {
        bool elevado = EsAdministrador();

        if (elevado && GetCpuTemperatureViaLibreHardwareMonitor(actualizar) is float real) return real;
        if (GetCpuTemperatureViaThermalZoneCounter() is float zona) return zona;
        if (elevado && GetCpuTemperatureViaWmiAcpi() is float acpi) return acpi;

        return 0f;
    }

    private static float? GetCpuTemperatureViaLibreHardwareMonitor(bool actualizar = true)
    {
        if (actualizar) ActualizarLecturas();

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            var validos = hardware.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && TieneLecturaValida(s))
                .ToList();

            if (validos.Count == 0) continue;

            foreach (string nombre in _sensoresTempPreferidos)
            {
                ISensor? preferido = validos.FirstOrDefault(
                    s => s.Name.Contains(nombre, StringComparison.OrdinalIgnoreCase));

                if (preferido is not null) return preferido.Value.GetValueOrDefault();
            }

            return validos.Max(s => s.Value.GetValueOrDefault());
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static float? GetCpuTemperatureViaThermalZoneCounter()
    {
        try
        {
            using var buscador = new ManagementObjectSearcher(
                @"root\CIMV2", "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

            float? maxima = null;

            foreach (ManagementBaseObject zona in buscador.Get())
            {
                using (zona)
                {
                    if (zona["Temperature"] is not (byte or ushort or uint or ulong)) continue;

                    float celsius = (float)(Convert.ToDouble(zona["Temperature"]) - 273.15);

                    if (celsius >= TempMinValida && celsius <= TempMaxValida)
                    {
                        maxima = maxima is null ? celsius : Math.Max(maxima.Value, celsius);
                    }
                }
            }

            return maxima;
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static float? GetCpuTemperatureViaWmiAcpi()
    {
        try
        {
            using var buscador = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            foreach (ManagementBaseObject zona in buscador.Get())
            {
                using (zona)
                {
                    if (zona["CurrentTemperature"] is not (byte or ushort or uint or ulong)) continue;

                    double deciKelvin = Convert.ToDouble(zona["CurrentTemperature"]);
                    double celsius = (deciKelvin / 10.0) - 273.15;

                    if (celsius >= TempMinValida && celsius <= TempMaxValida)
                        return (float)celsius;
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    public static bool EsAdministrador()
    {
        if (!OperatingSystem.IsWindows()) return false;

        using WindowsIdentity identidad = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identidad);

        if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return true;

        var sidSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        return identidad.User?.Equals(sidSystem) == true;
    }

    public static void ImprimirDiagnostico()
    {
        ActualizarLecturas();

        Console.WriteLine($"Ejecutando como Administrador: {(EsAdministrador() ? "SÍ" : "NO")}");
        Console.WriteLine();

        foreach (IHardware hardware in _computer.Hardware)
        {
            var sensores = hardware.Sensors.ToList();
            Console.WriteLine($"[{hardware.HardwareType}] {hardware.Name} — {sensores.Count} sensor(es) en total");

            foreach (ISensor sensor in sensores.Where(s => s.SensorType == SensorType.Temperature))
            {
                string lectura = sensor.Value is float valor ? $"{valor:F1} °C" : "sin valor";
                string estado = TieneLecturaValida(sensor) ? "" : "   <-- lectura no válida";
                Console.WriteLine($"    Temperatura · {sensor.Name}: {lectura}{estado}");
            }
        }

        Console.WriteLine();
        bool elevado = EsAdministrador();
        Console.WriteLine($"Proceso elevado (Administrador/SYSTEM): {(elevado ? "sí" : "no")}");
        Console.WriteLine("Fuentes de temperatura, en el orden en que se usan:");

        float? real = elevado ? GetCpuTemperatureViaLibreHardwareMonitor() : null;
        Console.WriteLine(!elevado
            ? "  1) LibreHardwareMonitor (CPU real): no se intenta, requiere Administrador/SYSTEM"
            : real is float r ? $"  1) LibreHardwareMonitor (CPU real): {r:F1} °C"
            : "  1) LibreHardwareMonitor (CPU real): sin lectura (¿falta el driver PawnIO?)");

        float? zona = GetCpuTemperatureViaThermalZoneCounter();
        Console.WriteLine(zona is float z
            ? $"  2) Contador Thermal Zone Information (sin Administrador): {z:F1} °C"
            : "  2) Contador Thermal Zone Information (sin Administrador): sin lectura en este equipo");

        float? acpi = elevado ? GetCpuTemperatureViaWmiAcpi() : null;
        Console.WriteLine(!elevado
            ? "  3) ACPI MSAcpi_ThermalZoneTemperature: no se intenta, Windows exige Administrador"
            : acpi is float a ? $"  3) ACPI MSAcpi_ThermalZoneTemperature: {a:F1} °C"
            : "  3) ACPI MSAcpi_ThermalZoneTemperature: sin lectura en este equipo");

        Console.WriteLine();
        float temperatura = GetCpuTemperature();
        Console.WriteLine(temperatura > 0f
            ? $"Temperatura de CPU que se enviaría a la API: {temperatura:F1} °C"
            : "Temperatura de CPU que se enviaría a la API: 0 (ninguna fuente tuvo lectura válida)");
    }

    public static void Cerrar()
    {
        if (_abierto)
        {
            _computer.Close();
            _abierto = false;
        }
    }
}