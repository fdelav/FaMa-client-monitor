namespace FaMaClientMonitor;

public static class Rutas
{
    private static string? _datos;

    public static string Datos => _datos ?? AppContext.BaseDirectory;

    public static void Inicializar(string? carpetaDatos)
    {
        if (string.IsNullOrWhiteSpace(carpetaDatos)) return;

        string completa = Path.GetFullPath(carpetaDatos.Trim());
        Directory.CreateDirectory(completa);
        _datos = completa;
    }
}
