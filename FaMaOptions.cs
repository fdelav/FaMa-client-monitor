namespace FaMaClientMonitor;

// Configuración del cliente, leída de la sección "FaMa" de appsettings.json.
// Los valores de abajo son los de respaldo: se usan si el archivo no existe o
// si falta alguna clave. Orden de prioridad (gana el de más abajo):
//   1. appsettings.json (junto al .exe)
//   2. appsettings.Production.json, si existe (para ajustes de un solo equipo)
//   3. Variables de entorno FaMa__ApiUrl, FaMa__PcIdManual, ...
//   4. Las variables antiguas FAMA_API_URL y FAMA_PC_ID (siguen funcionando)
//   5. Argumentos de línea de comandos: --FaMa:ApiUrl=https://...
public sealed class FaMaOptions
{
    public const string Seccion = "FaMa";

    // Dirección base de la API, sin barra final. Para ngrok: https://xxxx.ngrok-free.dev
    public string ApiUrl { get; set; } = "http://localhost:5000";

    // Carpeta donde se guardan la cola local, el registro del equipo y los logs.
    // Vacío = la carpeta del .exe (instalación por usuario). La instalación como
    // servicio la apunta a ProgramData.
    public string? DatosDir { get; set; }

    // PcId que se usa solo si el equipo no se pudo enrolar todavía.
    public int PcIdManual { get; set; } = 1;

    // Cada cuántos segundos se leen los sensores. Cada lectura se guarda en la
    // cola local (SQLite) y espera allí hasta el siguiente envío.
    public int IntervaloLecturaSegundos { get; set; } = 10;

    // Cada cuántos segundos se envían a la API las lecturas acumuladas, en
    // lotes (POST /agent/statusreport/batch). Con 10 s de lectura y 60 s de
    // envío, cada lote lleva unas 6 lecturas.
    public int IntervaloEnvioSegundos { get; set; } = 60;

    // Variación aleatoria, en segundos (±), que se aplica a CADA espera del envío.
    // Con varios PC arrancando a la vez (por ejemplo al empezar una clase), un
    // intervalo exacto haría que todos enviaran en el mismo instante; con esta
    // variación sus envíos se van repartiendo solos. Con 5 y un envío de 60 s,
    // cada espera dura entre 55 y 65 s. Con 0 se desactiva. No afecta a la hora
    // de las lecturas: cada una conserva el momento real en que se tomó.
    public int JitterEnvioSegundos { get; set; } = 5;

    // Máximo de lecturas por petición de lote (la API acepta hasta 500). Solo
    // importa cuando hay muchas acumuladas, por ejemplo tras una caída de la API.
    public int TamanoLote { get; set; } = 100;

    // Si es true, cada ciclo escribe en el log el JSON exacto que se envía a la
    // API (sin el ApiKey, que va en un encabezado y nunca se registra).
    public bool MostrarJson { get; set; } = false;
}
