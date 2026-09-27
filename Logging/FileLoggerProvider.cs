using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FaMaClientMonitor.Logging;

// Proveedor de logging mínimo que escribe a un archivo de texto, uno por día,
// en la carpeta indicada (por defecto "logs" junto al ejecutable).
//
// Se usa JUNTO al logging de consola (que el Host agrega por defecto), no en
// su lugar: en consola se ve en vivo mientras corres el programa a mano; el
// archivo queda disponible para revisar después, que es lo que hace falta en
// cuanto el cliente corra en segundo plano (tarea programada, servicio, etc.)
// y no haya ninguna consola donde mirar.
public sealed class FileLoggerProvider(string carpetaLogs) : ILoggerProvider
{
    private readonly object _bloqueoArchivo = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, nombre => new FileLogger(nombre, carpetaLogs, _bloqueoArchivo));

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger(string categoria, string carpetaLogs, object bloqueoArchivo) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string linea = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {categoria}: {formatter(state, exception)}";
            if (exception is not null) linea += Environment.NewLine + exception;

            Directory.CreateDirectory(carpetaLogs);
            string rutaArchivo = Path.Combine(carpetaLogs, $"cliente-{DateTime.Now:yyyyMMdd}.log");

            // Un solo bloqueo compartido entre todas las categorías: varios
            // hilos pueden escribir al mismo archivo al mismo tiempo.
            lock (bloqueoArchivo)
            {
                File.AppendAllText(rutaArchivo, linea + Environment.NewLine);
            }
        }
    }
}

public static class FileLoggingExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string carpetaLogs)
    {
        builder.AddProvider(new FileLoggerProvider(carpetaLogs));
        return builder;
    }
}
