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

    // Retorna la temperatura del "paquete" de CPU en grados Celsius.
    // Requiere permisos de Administrador en la mayoría de los equipos.
    public static float GetCpuTemperature()
    {
        ActualizarLecturas();

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            var sensoresTemp = hardware.Sensors
                .Where(s => s.SensorType == SensorType.Temperature)
                .ToList();

            // Preferimos el sensor "Package"/"Tctl" (temperatura general del
            // CPU); si no existe, tomamos el primer sensor de temperatura
            // disponible como respaldo.
            ISensor? paquete = sensoresTemp.FirstOrDefault(
                s => s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Average"));

            paquete ??= sensoresTemp.FirstOrDefault();

            if (paquete?.Value is float valor) return valor;
        }

        return 0f;
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
