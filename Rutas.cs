namespace FaMaClientMonitor;

// Carpeta donde el cliente guarda SUS DATOS: la cola local (monitoreo.db), el
// registro del equipo (enrollment.dat) y los logs.
//
// Por defecto es la carpeta del .exe, como siempre (instalación por usuario, sin
// Administrador). Con --FaMa:DatosDir=<carpeta> se mueve a otro sitio. La
// instalación como servicio la usa para dejar el programa en "Program Files"
// (que los usuarios no pueden modificar) y los datos en "ProgramData" con
// permisos restringidos. La configuración (appsettings.json) NO se mueve: se
// sigue leyendo junto al .exe, es decir, de una carpeta que solo un
// administrador puede cambiar.
public static class Rutas
{
    private static string? _datos;

    public static string Datos => _datos ?? AppContext.BaseDirectory;

    // Se llama una sola vez, al arrancar, antes de usar la cola, el registro o el log.
    public static void Inicializar(string? carpetaDatos)
    {
        if (string.IsNullOrWhiteSpace(carpetaDatos)) return;

        string completa = Path.GetFullPath(carpetaDatos.Trim());
        Directory.CreateDirectory(completa);
        _datos = completa;
    }
}
