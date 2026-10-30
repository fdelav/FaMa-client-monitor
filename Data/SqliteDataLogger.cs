using System.Globalization;
using FaMaClientMonitor.Monitors;
using Microsoft.Data.Sqlite;

namespace FaMaClientMonitor.Data;

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
                temp REAL NOT NULL,
                disk_activity REAL NOT NULL DEFAULT 0,
                disk_response_ms REAL NOT NULL DEFAULT 0
            );
            """;
        comando.ExecuteNonQuery();

        foreach (string columna in new[] { "disk_activity", "disk_response_ms" })
        {
            comando.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('pendientes') WHERE name = '{columna}';";
            if (Convert.ToInt64(comando.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                comando.CommandText = $"ALTER TABLE pendientes ADD COLUMN {columna} REAL NOT NULL DEFAULT 0;";
                comando.ExecuteNonQuery();
            }
        }

        foreach (string columna in new[] { "disk_read_bps", "disk_write_bps" })
        {
            comando.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('pendientes') WHERE name = '{columna}';";
            if (Convert.ToInt64(comando.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                comando.CommandText = $"ALTER TABLE pendientes DROP COLUMN {columna};";
                comando.ExecuteNonQuery();
            }
        }

        _tablaCreada = true;
    }

    public static void GuardarPendiente(string rutaArchivo, int pcId, float ram, float cpu, float gpu, float temperatura, DiskMonitor.Lectura disco, DateTime momentoLectura)
    {
        lock (_bloqueo)
        {
            AsegurarTabla(rutaArchivo);

            using var conexion = new SqliteConnection(CadenaConexion(rutaArchivo));
            conexion.Open();

            using var comando = conexion.CreateCommand();
            comando.CommandText =
                """
                INSERT INTO pendientes (created_at, pc_id, ram, cpu, gpu, temp,
                                        disk_activity, disk_response_ms)
                VALUES ($createdAt, $pcId, $ram, $cpu, $gpu, $temp,
                        $diskActivity, $diskResponse);
                """;

            comando.Parameters.AddWithValue("$createdAt", momentoLectura.ToString("o", CultureInfo.InvariantCulture));
            comando.Parameters.AddWithValue("$pcId", pcId);
            comando.Parameters.AddWithValue("$ram", ram);
            comando.Parameters.AddWithValue("$cpu", cpu);
            comando.Parameters.AddWithValue("$gpu", gpu);
            comando.Parameters.AddWithValue("$temp", temperatura);
            comando.Parameters.AddWithValue("$diskActivity", disco.ActividadPct);
            comando.Parameters.AddWithValue("$diskResponse", disco.RespuestaMs);

            comando.ExecuteNonQuery();
        }
    }

    public record Pendiente(
        long Id, int PcId, float Ram, float Cpu, float Gpu, float Temp, DateTime CreatedAt,
        float DiskActivity, float DiskResponseMs);

    public static List<Pendiente> ObtenerPendientes(string rutaArchivo)
    {
        lock (_bloqueo)
        {
            AsegurarTabla(rutaArchivo);

            using var conexion = new SqliteConnection(CadenaConexion(rutaArchivo));
            conexion.Open();

            using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT id, pc_id, ram, cpu, gpu, temp, created_at, disk_activity, disk_response_ms FROM pendientes ORDER BY id;";

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
                    createdAt,
                    lector.GetFloat(7),
                    lector.GetFloat(8)
                ));
            }

            return resultado;
        }
    }

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
