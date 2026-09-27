using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FaMaClientMonitor.Data;

// Guarda en disco el resultado del enrolamiento (el Id que la API asignó a
// este equipo y su ApiKey) para no repetir el proceso en cada arranque.
//
// El ApiKey NO se guarda en texto plano ni en monitoreo.db: se cifra con
// DPAPI (Windows Data Protection API), el mismo mecanismo que usa Windows
// para credenciales guardadas del propio sistema operativo. No requiere que
// el cliente maneje ninguna clave propia; Windows la deriva del equipo.
//
// Se usa DataProtectionScope.LocalMachine (no CurrentUser) porque este
// programa puede ejecutarse bajo distintas cuentas (una sesión de usuario,
// una tarea programada como SYSTEM, etc.) y en todos los casos debe poder
// leer el mismo archivo. La contrapartida: cualquier proceso que corra en
// esta misma máquina puede pedirle a Windows que lo descifre, igual que
// ocurre con cualquier credencial guardada a nivel de equipo. Lo que si
// protege de verdad es que el archivo, copiado a otro PC, quede inservible:
// DPAPI no puede descifrarlo fuera de la máquina donde se cifró.
public static class EnrollmentStore
{
    public record Enrollment(int ClientId, Guid ApiKey, string Uuid, DateTime EnrolledAtUtc);

    private static string RutaArchivo => Path.Combine(AppContext.BaseDirectory, "enrollment.dat");

    // Devuelve el enrolamiento guardado, o null si no existe, está dañado, o
    // no se puede descifrar (por ejemplo, el archivo se copió desde otro PC).
    public static Enrollment? Cargar()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!File.Exists(RutaArchivo)) return null;

        try
        {
            byte[] cifrado = File.ReadAllBytes(RutaArchivo);
            byte[] plano = ProtectedData.Unprotect(cifrado, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<Enrollment>(plano);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Enrolamiento] No se pudo leer el enrolamiento guardado ({ex.GetType().Name}); se volverá a enrolar el equipo.");
            return null;
        }
    }

    public static void Guardar(Enrollment enrollment)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("[Enrolamiento] DPAPI solo está disponible en Windows; no se guardó el enrolamiento.");
            return;
        }

        byte[] plano = JsonSerializer.SerializeToUtf8Bytes(enrollment);
        byte[] cifrado = ProtectedData.Protect(plano, optionalEntropy: null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(RutaArchivo, cifrado);
    }

    // Borra el enrolamiento guardado (ej. si la API confirma que ya no es
    // válido). El equipo se enrolará de nuevo en el próximo arranque.
    public static void Eliminar()
    {
        if (File.Exists(RutaArchivo)) File.Delete(RutaArchivo);
    }
}
