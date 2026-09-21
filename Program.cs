using FaMaClientMonitor.Monitors;

Console.WriteLine("Hello, World!");

float cpu = WindowsStatusMonitor.GetCpuUsage();
float ram = WindowsStatusMonitor.GetRamUsage();
float gpu = WindowsStatusMonitor.GetGpuUsage();
float temperature = WindowsStatusMonitor.GetCpuTemperature();

Console.WriteLine($"CPU usage: {cpu}%");
Console.WriteLine($"RAM usage: {ram}%");
Console.WriteLine($"GPU usage: {gpu}%");
Console.WriteLine($"CPU temperature: {temperature}°C");
