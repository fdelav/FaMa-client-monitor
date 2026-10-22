using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace FaMaClientMonitor.Monitors;

public static class WindowsStatusMonitor
{
    // Computer es el punto de entrada de LibreHardwareMonitorLib: representa
    // el equipo local y expone el hardware detectado (CPU, GPU, RAM, etc.)
    private static readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
    };

    private static bool _abierto = false;

    // Abre el acceso a los sensores. Debe ejecutarse una sola vez.
    // IMPORTANTE: leer temperatura de CPU normalmente requiere ejecutar
    // la aplicación como Administrador; si no se ejecuta como admin,
    // los sensores de load (CPU/RAM/GPU %) igual funcionan, pero la
    // temperatura puede devolver 0.
    private static void AsegurarAbierto()
    {
        if (!_abierto)
        {
            _computer.Open();
            _abierto = true;
        }
    }

    // Refresca las lecturas de todos los sensores antes de consultarlos.
    // LibreHardwareMonitorLib no actualiza automáticamente: hay que
    // llamar Update() en cada hardware (y sub-hardware) antes de leer.
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

    // Refresca todos los sensores UNA vez. El Worker lo llama una sola vez por
    // lectura y luego consulta CPU, RAM, GPU y temperatura con actualizar:false;
    // así no se recorre el hardware completo una vez por cada valor.
    public static void Actualizar() => ActualizarLecturas();

    // Retorna el porcentaje de uso de CPU (0.0 a 100.0).
    // Todos los Get* aceptan actualizar:false para reutilizar la última
    // actualización (ver Actualizar()); por defecto refrescan por su cuenta.
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

    // Retorna el porcentaje de uso de la RAM
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

    // Retorna el porcentaje de uso de la GPU (núcleo/core).
    // Si el equipo tiene varias GPUs (ej. integrada + dedicada), retorna
    // la primera con lectura de carga disponible.
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

    // Rango en el que una temperatura de CPU se considera una lectura real.
    // Un sensor sin dato (o sin driver/permisos) suele devolver 0 o un valor
    // absurdo; eso NO se debe reportar como "el CPU está a 0 °C".
    private const float TempMinValida = 1f;
    private const float TempMaxValida = 125f;

    // Nombres de sensor, de más a menos representativos de la temperatura
    // general del CPU. Intel expone "CPU Package"; AMD Ryzen expone
    // "Core (Tctl/Tdie)". Se compara sin distinguir mayúsculas.
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

    // Retorna la temperatura general del CPU en grados Celsius, o 0 si no hay
    // ninguna lectura válida (ver ImprimirDiagnostico para saber por qué).
    //
    // Orden de fuentes (de más a menos fiable):
    //   1. LibreHardwareMonitor: temperatura REAL del CPU. Necesita el driver
    //      PawnIO instalado y que el proceso corra como Administrador o SYSTEM.
    //      Solo se intenta si el proceso ya está elevado, así en una cuenta
    //      normal nunca se toca el acceso a hardware de bajo nivel.
    //   2. Contador "Thermal Zone Information" (WMI root\CIMV2): no requiere
    //      Administrador, pero es la zona térmica que reporta el firmware (a
    //      menudo placa/chasis, no el núcleo del CPU) y varía poco con la carga.
    //   3. ACPI MSAcpi_ThermalZoneTemperature (WMI root\WMI): misma idea que la
    //      anterior, pero Windows SOLO deja consultarla elevado.
    // En los equipos institucionales el cliente corre SIN Administrador (no hay
    // servicio, ni tarea como SYSTEM, ni driver), así que la única fuente que
    // aplica allí es la 2. Las fuentes 1 y 3 quedan para pruebas en un equipo
    // propio abierto como Administrador.
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

            // No hay un sensor "general": se toma el núcleo más caliente.
            return validos.Max(s => s.Value.GetValueOrDefault());
        }

        return null;
    }

    // Contador de rendimiento "Thermal Zone Information" expuesto por WMI en
    // root\CIMV2 (a diferencia de root\WMI, no suele exigir Administrador).
    // Temperature viene en Kelvin. Si hay varias zonas se toma la más caliente.
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
            // La clase no existe en este equipo.
        }
        catch (UnauthorizedAccessException)
        {
            // Falta permiso para consultarla.
        }

        return null;
    }

    // Zona térmica ACPI (MSAcpi_ThermalZoneTemperature, WMI root\WMI). No necesita
    // driver, pero Windows solo deja consultarla elevado (Administrador/SYSTEM);
    // sin eso lanza "acceso denegado". CurrentTemperature viene en décimas de
    // Kelvin. Puede no existir o devolver un valor fijo de relleno, por eso se
    // valida el rango.
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
            // La clase MSAcpi_ThermalZoneTemperature no existe en este equipo.
        }
        catch (UnauthorizedAccessException)
        {
            // Falta permiso para consultar ese namespace de WMI en este equipo.
        }

        return null;
    }

    // ¿El proceso corre con permisos de Administrador? Sin ellos el driver de
    // bajo nivel no se puede usar y la temperatura del CPU no se lee.
    // Sirve tanto para un usuario elevado a Administrador como para la
    // cuenta SYSTEM (la que usa, por ejemplo, una tarea programada con
    // /RU SYSTEM). SYSTEM tiene privilegios equivalentes o superiores a
    // Administrador en la práctica, pero WindowsIdentity no siempre la
    // reporta como miembro del grupo "Administradores" — por eso se
    // comprueba también el SID bien conocido de SYSTEM directamente.
    public static bool EsAdministrador()
    {
        if (!OperatingSystem.IsWindows()) return false;

        using WindowsIdentity identidad = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identidad);

        if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return true;

        var sidSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        return identidad.User?.Equals(sidSystem) == true;
    }

    // Modo diagnóstico (dotnet run -- --sensores): lista el hardware detectado
    // y todos sus sensores de temperatura, para ver qué está viendo realmente
    // la librería y por qué la temperatura del CPU sale (o no) bien.
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

    // Libera los recursos de LibreHardwareMonitorLib. Llamar al cerrar la app.
    public static void Cerrar()
    {
        if (_abierto)
        {
            _computer.Close();
            _abierto = false;
        }
    }
}