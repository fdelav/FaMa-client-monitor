namespace FaMaClientMonitor;

public sealed class FaMaOptions
{
    public const string Seccion = "FaMa";

    public string ApiUrl { get; set; } = "http://localhost:5000";

    public string? DatosDir { get; set; }

    public int PcIdManual { get; set; } = 1;

    public int IntervaloLecturaSegundos { get; set; } = 10;

    public int IntervaloEnvioSegundos { get; set; } = 60;

    public int JitterEnvioSegundos { get; set; } = 5;

    public int TamanoLote { get; set; } = 100;

    public bool MostrarJson { get; set; } = false;
}
