using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FaMaClientMonitor.Data;

// Cola de respaldo local: guarda lecturas que NO se pudieron enviar a la
// API (ej. sin conexión al servidor), para reintentarlas más tarde. Si el
// envío a la API funciona a la primera, el dato nunca pasa por aquí.
public static class SqliteDataLogger
{
    private static readonly object _bloqueo = new();
    private static bool _tablaCreada = false;

    private static string CadenaConexion(string rutaArchivo) => $"Data Source={rutaArchivo}";

    private static void AsegurarTabla(string rutaArchivo)
    {
        if (_tablaCreada) return;

        using var conexion = new SqliteConnection(CadenaConexion(rutaArchivo));
        conexion.Open();

        using var comando = conexion.CreateCommand();

        // Si el archivo .db viene de una versión anterior del cliente, la tabla
        // "pendientes" existe pero con la columna "timestamp" en vez de
        // "created_at". CREATE TABLE IF NOT EXISTS no la modifica, y las
        // consultas fallarían con "no such column". En ese caso se conserva la
        // tabla vieja renombrada (no se pierde nada) y se crea una nueva.
        comando.CommandText = "SELECT COUNT(*) FROM pragma_table_info('pendientes');";
        long columnasTotales = Convert.ToInt64(comando.ExecuteScalar(), CultureInfo.InvariantCulture);

        comando.CommandText = "SELECT COUNT(*) FROM pragma_table_info('pendientes') WHERE name = 'created_at';";
        long columnasCreatedAt = Convert.ToInt64(comando.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (columnasTotales > 0 && columnasCreatedAt == 0)
        {
            string nombreRespaldo = $"pendientes_esquema_viejo_{DateTime.UtcNow:yyyyMMddHHmmss}";
            comando.CommandText = $"ALTER TABLE pendientes RENAME TO {nombreRespaldo};";
            comando.ExecuteNonQuery();
            Console.WriteLine($"[SQLite] Esquema antiguo detectado: tabla anterior conservada como '{nombreRespaldo}'.");
        }

        comando.CommandText =
            """
            CREATE TABLE IF NOT EXISTS pendientes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                pc_id INTEGER NOT NULL,
                ram REAL NOT NULL,
                cpu REAL NOT NULL,
                gpu REAL NOT NULL,
                temp REAL NOT NULL
            );
            """;
        comando.ExecuteNonQuery();

        _tablaCreada = true;
    }

    // Guarda una lectura que no se pudo enviar, para reintentar después.
    // "momentoLectura" es la hora REAL en que se leyeron los sensores (UTC),
    // no la hora en que eventualmente se reintente el envío.
    public static void GuardarPendiente(string rutaArchivo, int pcId, float ram, float cpu, float gpu, float temperatura, DateTime momentoLectura)
    {
        lock (_bloqueo)
        {
            AsegurarTabla(rutaArchivo);

            using var conexion = new SqliteConnection(CadenaConexion(rutaArchivo));
            conexion.Open();

            using var comando = conexion.CreateCommand();
            comando.CommandText =
                """
                INSERT INTO pendientes (created_at, pc_id, ram, cpu, gpu, temp)
                VALUES ($createdAt, $pcId, $ram, $cpu, $gpu, $temp);
                """;

            // Formato "o" (round-trip ISO 8601) preserva la hora exacta y la
            // zona (UTC) para poder reconstruir el DateTime sin ambigüedad.
            comando.Parameters.AddWithValue("$createdAt", momentoLectura.ToString("o", CultureInfo.InvariantCulture));
            comando.Parameters.AddWithValue("$pcId", pcId);
            comando.Parameters.AddWithValue("$ram", ram);
            comando.Parameters.AddWithValue("$cpu", cpu);
            comando.Parameters.AddWithValue("$gpu", gpu);
            comando.Parameters.AddWithValue("$temp", temperatura);

            comando.ExecuteNonQuery();
        }
    }

    public record Pendiente(long Id, int PcId, float Ram, float Cpu, float Gpu, float Temp, DateTime CreatedAt);

    // Retorna todas las lecturas que siguen esperando ser enviadas.
    public static List<Pendiente> ObtenerPendientes(string rutaArchivo)
    {
        lock (_bloqueo)
        {
            AsegurarTabla(rutaArchivo);

            using var conexion = new SqliteConnection(CadenaConexion(rutaArchivo));
            conexion.Open();

            using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT id, pc_id, ram, cpu, gpu, temp, created_at FROM pendientes ORDER BY id;";

            using var lector = comando.ExecuteReader();
            var resultado = new List<Pendiente>();

            while (lector.Read())
            {
                DateTime createdAt = DateTime.Parse(
                    lector.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                resultado.Add(new Pendiente(
                    lector.GetInt64(0),
                    lector.GetInt32(1),
                    lector.GetFloat(2),
                    lector.GetFloat(3),
                    lector.GetFloat(4),
                    lector.GetFloat(5),
                    createdAt
                ));
            }

            return resultado;
        }
    }

    // Borra varios pendientes ya enviados exitosamente (en una sola transacción).
    public static void EliminarPendientes(string rutaArchivo, IEnumerable<long> ids)
    {
        lock (_bloqueo)
        {
            AsegurarTabla(rutaArchivo);

            using var conexion = new SqliteConnection(CadenaConexion(rutaArchivo));
            conexion.Open();

            using var transaccion = conexion.BeginTransaction();
            using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = "DELETE FROM pendientes WHERE id = $id;";

            var parametroId = comando.CreateParameter();
            parametroId.ParameterName = "$id";
            comando.Parameters.Add(parametroId);

            foreach (long id in ids)
            {
                parametroId.Value = id;
                comando.ExecuteNonQuery();
            }

            transaccion.Commit();
        }
    }
}
