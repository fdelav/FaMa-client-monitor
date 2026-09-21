namespace FaMaClientMonitor.Monitors;

public static class WindowsStatusMonitor
{
    // Retorna el porcentaje de uso de CPU (0.0 a 100.0)
    public static float GetCpuUsage()
    {
        // TODO: Implementar usando PerformanceCounter
        return 0.0f;
    }

    // Retorna el porcentaje de uso de la RAM
    public static float GetRamUsage()
    {
        // TODO: Implementar lectura de RAM
        return 0f;
    }

    // Retorna el porcentaje de uso de la GPU
    public static float GetGpuUsage()
    {
        // TODO: Implementar lectura de GPU
        return 0f;
    }

    public static float GetCpuTemperature()
    {
        // TODO: Implementar lectura de temperatura de CPU
        // Posiblemente necesite libreria extra
        // Menos prioridad, si dificulta las cosas no colocar
        return 0f;
    }
}
