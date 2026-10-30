using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FaMaClientMonitor.Data;

public static class EnrollmentStore
{
    public record Enrollment(int ClientId, Guid ApiKey, string Uuid, DateTime EnrolledAtUtc);

    private static string RutaArchivo => Path.Combine(Rutas.Datos, "enrollment.dat");

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

    public static void Eliminar()
    {
        if (File.Exists(RutaArchivo)) File.Delete(RutaArchivo);
    }
}
