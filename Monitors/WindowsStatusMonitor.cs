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

    // Retorna el porcentaje de uso de CPU (0.0 a 100.0)
    public static float GetCpuUsage()
    {
        ActualizarLecturas();

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
    public static float GetRamUsage()
    {
        ActualizarLecturas();

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
    public static float GetGpuUsage()
    {
        ActualizarLecturas();

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
    // Para leer la temperatura del CPU hacen falta DOS cosas:
    //   1. Ejecutar como Administrador.
    //   2. Tener instalado el driver PawnIO (LibreHardwareMonitorLib 0.9.5+
    //      lo usa en lugar de WinRing0): winget install PawnIO.PawnIO
    public static float GetCpuTemperature()
    {
        ActualizarLecturas();

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

        return 0f;
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
        float temperatura = GetCpuTemperature();
        Console.WriteLine(temperatura > 0f
            ? $"Temperatura de CPU que se enviaría a la API: {temperatura:F1} °C"
            : "Temperatura de CPU que se enviaría a la API: 0 (no hay ninguna lectura válida)");
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