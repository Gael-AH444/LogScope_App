#:property PublishAot=false
// (La línea de arriba evita que .NET prepare compilación nativa AOT; aquí no la necesitamos.)
// generar-logs.cs — Generador de logs de acceso de prueba para LogScope.
// App de un solo archivo (.NET 10+). Uso:
//   dotnet run generar-logs.cs -- [carpeta] [archivos] [lineasPorArchivo] [tasaCorrupcion]
// Ejemplos:
//   dotnet run generar-logs.cs -- ../datos-muestra 8 200000
//   dotnet run generar-logs.cs -- ../datos-mini 1 50 0.2
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

string carpeta        = args.Length > 0 ? args[0] : "datos-muestra";
int numArchivos       = args.Length > 1 ? int.Parse(args[1]) : 8;
int lineasPorArchivo  = args.Length > 2 ? int.Parse(args[2]) : 200_000;
double tasaCorrupcion  = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0.005; // 0.5 % por defecto

// (ruta, peso relativo de tráfico, ms base de respuesta, probabilidad de error)
var endpoints = new (string Ruta, int Peso, int MsBase, double TasaError)[]
{
    ("/",                    20,  15, 0.01),
    ("/index.html",          10,  10, 0.005),
    ("/api/productos",       18,  80, 0.02),
    ("/api/productos/{id}",  15,  40, 0.03),
    ("/api/carrito",          8, 120, 0.05),
    ("/api/checkout",         4, 900, 0.12), // lento y con errores
    ("/api/buscar",          10, 350, 0.04), // lento
    ("/login",                6, 200, 0.08),
    ("/admin",                2,  60, 0.40), // casi todo 403
    ("/static/app.js",       12,   5, 0.002),
    ("/static/styles.css",   12,   5, 0.002),
    ("/favicon.ico",          6,   2, 0.30), // 404 frecuente
};
int pesoTotalEndpoints = endpoints.Sum(e => e.Peso);

// Tráfico por hora del día: madrugada baja, picos al mediodía y en la noche.
int[] pesoHora = [2, 1, 1, 1, 1, 2, 4, 6, 8, 9, 10, 11, 13, 12, 10, 9, 9, 10, 12, 14, 13, 9, 6, 4];
long pesoTotalHoras = pesoHora.Sum();

string[] agentesNavegador =
[
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
    "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0",
    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",
];
string[] ipsBots    = ["203.0.113.66", "198.51.100.23", "192.0.2.200"];
string[] agentesBot = ["python-requests/2.32", "curl/8.9.1", "Go-http-client/1.1"];

var statusValido = new Regex("\" \\d{3} "); // para corromper el status
var dia = new DateTime(2026, 9, 30);
Directory.CreateDirectory(carpeta);
int corruptasTotales = 0;

for (int n = 1; n <= numArchivos; n++)
{
    var rng = new Random(1000 + n); // semilla fija → mismos datos en cada corrida
    // Pool de IPs "humanas" en rangos reservados para documentación (TEST-NET).
    var poolIps = Enumerable.Range(0, 600)
        .Select(_ => $"{(rng.Next(3) switch { 0 => "192.0.2", 1 => "198.51.100", _ => "203.0.113" })}.{rng.Next(1, 255)}")
        .ToArray();

    string ruta = Path.Combine(carpeta, $"web-{n:00}.log");
    using var writer = new StreamWriter(ruta, append: false, new UTF8Encoding(false)) { NewLine = "\n" };

    int corruptas = 0;
    int escritas = 0;
    for (int h = 0; h < 24; h++)
    {
        // Reparto exacto de líneas por hora (aritmética entera acumulada, sin redondeos perdidos).
        long acumAntes = pesoHora.Take(h).Sum();
        long acumDespues = acumAntes + pesoHora[h];
        int lineasHora = (int)(lineasPorArchivo * acumDespues / pesoTotalHoras
                             - lineasPorArchivo * acumAntes / pesoTotalHoras);

        for (int i = 0; i < lineasHora; i++)
        {
            var momento = dia.AddHours(h).AddSeconds(i * 3600.0 / lineasHora);
            string linea = GenerarLinea(rng, momento, poolIps);

            if (rng.NextDouble() < tasaCorrupcion)
            {
                linea = Corromper(rng, linea, momento);
                corruptas++;
            }
            writer.WriteLine(linea);
            escritas++;
        }
    }
    corruptasTotales += corruptas;
    Console.WriteLine($"{ruta}: {escritas:N0} líneas ({corruptas:N0} corruptas a propósito)");
}
Console.WriteLine($"Listo. Total de líneas corruptas: {corruptasTotales:N0}");

string GenerarLinea(Random rng, DateTime momento, string[] poolIps)
{
    bool esBot = rng.NextDouble() < 0.15;
    int idxBot = rng.Next(ipsBots.Length);
    string ip = esBot ? ipsBots[idxBot] : poolIps[rng.Next(poolIps.Length)];
    string agente = esBot ? agentesBot[idxBot] : agentesNavegador[rng.Next(agentesNavegador.Length)];

    // Elección de endpoint ponderada por peso.
    int tirada = rng.Next(pesoTotalEndpoints);
    var ep = endpoints[0];
    foreach (var e in endpoints) { if (tirada < e.Peso) { ep = e; break; } tirada -= e.Peso; }

    string path = ep.Ruta.Replace("{id}", rng.Next(1, 5000).ToString());
    string metodo = ep.Ruta is "/api/carrito" or "/api/checkout" or "/login"
        ? (rng.NextDouble() < 0.7 ? "POST" : "GET")
        : "GET";

    int status;
    if (rng.NextDouble() < ep.TasaError)
        status = ep.Ruta switch
        {
            "/admin" => 403,
            "/favicon.ico" => 404,
            _ => rng.NextDouble() < 0.75 ? new[] { 400, 401, 404, 429 }[rng.Next(4)]
                                         : new[] { 500, 502, 503 }[rng.Next(3)],
        };
    else if (ep.Ruta.StartsWith("/static") && rng.NextDouble() < 0.2)
        status = 304;
    else
        status = metodo == "POST" ? 201 : 200;

    string bytes = status == 304 ? "-" : rng.Next(200, 60_000).ToString(); // "-" = 0 bytes en combined
    int ms = (int)(ep.MsBase * (0.5 + rng.NextDouble() * 1.5));
    if (status >= 500) ms += rng.Next(500, 3000);
    string referer = rng.NextDouble() < 0.5 ? "-" : "https://tienda.example.com/";

    // InvariantCulture: "Sep" en inglés, sin importar el idioma de tu sistema.
    string fecha = momento.ToString("dd/MMM/yyyy:HH:mm:ss", CultureInfo.InvariantCulture) + " -0600";

    // Formato "combined" + tiempo de respuesta en ms al final (extensión común).
    return $"{ip} - - [{fecha}] \"{metodo} {path} HTTP/1.1\" {status} {bytes} \"{referer}\" \"{agente}\" {ms}";
}

string Corromper(Random rng, string linea, DateTime momento) => rng.Next(6) switch
{
    0 => "",                                                                // línea vacía
    1 => linea[..rng.Next(10, linea.Length / 2)],                           // truncada
    2 => "#### log rotado: escritura interrumpida ####",                   // basura
    3 => statusValido.Replace(linea, "\" OK ", 1),                      // status no numérico
    4 => linea.Replace(momento.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture), "31/Foo/2026"), // fecha inválida
    _ => linea.Replace(" HTTP/1.1\"", " HTTP/1.1"),                         // comilla de cierre faltante
};

