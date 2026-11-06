using System.Globalization;

namespace Nexus.NetRoute.Geo;

/// <summary>Cidade com coordenadas (centro aproximado) e códigos que costumam aparecer no nome dos roteadores (rDNS).</summary>
public sealed record Place(string Id, string Name, double Latitude, double Longitude, string Country, IReadOnlyList<string> Codes)
{
    public bool InBrazil => Country == "BR";
}

/// <summary>
/// Cidades que o diagnóstico conhece: todas as capitais do Brasil, polos do interior e as cidades onde ficam servidores de jogos
/// e relays (América do Sul, EUA, Europa, Ásia). Coordenadas do centro da cidade: a distância é aproximada, e por isso a régua
/// usa uma margem (ver <see cref="PhysicsFloor"/>).
/// </summary>
public static class Places
{
    public static IReadOnlyList<Place> All { get; } =
    [
        // Brasil (capitais + polos com PTT do IX.br)
        new("sao-paulo", "São Paulo", -23.55, -46.63, "BR", ["gru", "sao", "spo", "saopaulo", "sampa"]),
        new("campinas", "Campinas", -22.91, -47.06, "BR", ["cpq", "vcp", "campinas"]),
        new("rio-de-janeiro", "Rio de Janeiro", -22.91, -43.17, "BR", ["gig", "rjo", "rio", "riodejaneiro"]),
        new("belo-horizonte", "Belo Horizonte", -19.92, -43.94, "BR", ["bhz", "cnf", "plu", "belohorizonte"]),
        new("vitoria", "Vitória", -20.32, -40.34, "BR", ["vix", "vitoria"]),
        new("curitiba", "Curitiba", -25.43, -49.27, "BR", ["cwb", "curitiba"]),
        new("florianopolis", "Florianópolis", -27.60, -48.55, "BR", ["fln", "florianopolis"]),
        new("porto-alegre", "Porto Alegre", -30.03, -51.23, "BR", ["poa", "portoalegre"]),
        new("brasilia", "Brasília", -15.79, -47.88, "BR", ["bsb", "brasilia"]),
        new("goiania", "Goiânia", -16.69, -49.26, "BR", ["gyn", "goiania"]),
        new("campo-grande", "Campo Grande", -20.47, -54.62, "BR", ["cgr", "campogrande"]),
        new("cuiaba", "Cuiabá", -15.60, -56.10, "BR", ["cgb", "cuiaba"]),
        new("salvador", "Salvador", -12.97, -38.50, "BR", ["ssa", "salvador"]),
        new("aracaju", "Aracaju", -10.91, -37.07, "BR", ["aju", "aracaju"]),
        new("maceio", "Maceió", -9.67, -35.74, "BR", ["mcz", "maceio"]),
        new("recife", "Recife", -8.05, -34.88, "BR", ["rec", "recife"]),
        new("joao-pessoa", "João Pessoa", -7.12, -34.86, "BR", ["jpa", "joaopessoa"]),
        new("natal", "Natal", -5.79, -35.21, "BR", ["nat", "natal"]),
        new("fortaleza", "Fortaleza", -3.73, -38.52, "BR", ["for", "ftz", "fortaleza"]),
        new("teresina", "Teresina", -5.09, -42.80, "BR", ["the", "teresina"]),
        new("sao-luis", "São Luís", -2.53, -44.30, "BR", ["slz", "saoluis"]),
        new("belem", "Belém", -1.46, -48.49, "BR", ["bel", "belem"]),
        new("macapa", "Macapá", 0.03, -51.07, "BR", ["mcp", "macapa"]),
        new("manaus", "Manaus", -3.12, -60.02, "BR", ["mao", "manaus"]),
        new("boa-vista", "Boa Vista", 2.82, -60.67, "BR", ["bvb", "boavista"]),
        new("porto-velho", "Porto Velho", -8.76, -63.90, "BR", ["pvh", "portovelho"]),
        new("rio-branco", "Rio Branco", -9.97, -67.81, "BR", ["rbr", "riobranco"]),
        new("palmas", "Palmas", -10.18, -48.33, "BR", ["pmw", "palmas"]),
        // América do Sul
        new("santiago", "Santiago", -33.45, -70.67, "CL", ["scl", "santiago"]),
        new("buenos-aires", "Buenos Aires", -34.60, -58.38, "AR", ["eze", "bue", "buenosaires"]),
        // EUA
        new("miami", "Miami", 25.76, -80.19, "US", ["mia", "miami"]),
        new("ashburn", "Ashburn (Virgínia)", 39.04, -77.49, "US", ["iad", "ash", "ashburn", "wdc"]),
        new("chicago", "Chicago", 41.88, -87.63, "US", ["ord", "chi", "chicago"]),
        new("dallas", "Dallas", 32.78, -96.80, "US", ["dfw", "dal", "dallas"]),
        new("los-angeles", "Los Angeles", 34.05, -118.24, "US", ["lax", "losangeles"]),
        // Europa
        new("lisboa", "Lisboa", 38.72, -9.14, "PT", ["lis", "lisbon", "lisboa"]),
        new("madrid", "Madri", 40.42, -3.70, "ES", ["mad", "madrid"]),
        new("frankfurt", "Frankfurt", 50.11, 8.68, "DE", ["fra", "frankfurt"]),
        new("londres", "Londres", 51.51, -0.13, "GB", ["lhr", "lon", "london"]),
        // Ásia
        new("toquio", "Tóquio", 35.68, 139.69, "JP", ["nrt", "hnd", "tyo", "tokyo"]),
        new("singapura", "Singapura", 1.35, 103.82, "SG", ["sin", "sgp", "singapore"]),
    ];

    public static Place? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var key = Normalize(idOrName);
        return All.FirstOrDefault(p => Normalize(p.Id) == key || Normalize(p.Name) == key);
    }

    /// <summary>
    /// Cidade sugerida pelo nome de um roteador (rDNS), procurando os códigos como "palavras" inteiras: "ae1.gru3.example.net"
    /// → São Paulo. Palavras soltas comuns ("for", "the", "rec", "bel", "nat") só valem se vierem coladas a dígitos ("for1",
    /// "rec02"), o jeito usual dos provedores, para não confundir com inglês.
    /// </summary>
    public static Place? FromHostName(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName)) return null;
        var tokens = Tokenize(hostName.ToLowerInvariant());
        foreach (var (letters, hadDigits) in tokens)
            foreach (var p in All)
                if (p.Codes.Contains(letters, StringComparer.Ordinal) && (hadDigits || letters.Length > 3 || !Ambiguous.Contains(letters)))
                    return p;
        return null;
    }

    private static readonly HashSet<string> Ambiguous = new(StringComparer.Ordinal) { "for", "the", "rec", "bel", "nat", "sin", "mad", "lon", "chi", "dal", "ash", "sao", "rio", "bue", "lis", "fra" };

    private static List<(string Letters, bool HadDigits)> Tokenize(string host)
    {
        var result = new List<(string, bool)>();
        foreach (var part in host.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            // "gru3" → ("gru", true); "saopaulo" → ("saopaulo", false); "xe-0-1-gru" já foi separado pelo hífen.
            var leading = new string(part.TakeWhile(char.IsAsciiLetter).ToArray());
            if (leading.Length < 3) continue;
            var rest = part[leading.Length..];
            if (rest.Length == 0) result.Add((leading, false));
            else if (rest.All(char.IsAsciiDigit)) result.Add((leading, true));
        }
        return result;
    }

    private static string Normalize(string s)
    {
        var formD = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(formD.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsAsciiLetterOrDigit(c)).ToArray());
    }
}

/// <summary>
/// Régua física. A luz na fibra percorre ~200.000 km/s, então ida e volta custam ~1 ms a cada 100 km em linha reta; a fibra
/// real faz curvas (1,3 a 2× a linha reta) e cada ponta tem alguns ms de acesso. Nenhum relay baixa o ping abaixo do piso;
/// ele só pode remover o EXCESSO acima do esperado.
/// </summary>
public static class PhysicsFloor
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>Folga fixa de acesso (Wi-Fi/cabo, provedor, servidor), somada ao limite "normal".</summary>
    public const double AccessOverheadMs = 5.0;

    /// <summary>Multiplicador do piso que ainda é rota boa (fibra real faz até ~2× a linha reta).</summary>
    public const double GoodRouteFactor = 2.0;

    public static double DistanceKm(Place a, Place b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(b.Latitude - a.Latitude);
        var dLon = Rad(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(a.Latitude)) * Math.Cos(Rad(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    /// <summary>Menor RTT fisicamente possível (ms) para uma distância em linha reta.</summary>
    public static double FloorMs(double distanceKm) => Math.Max(0, distanceKm) / 100.0;

    /// <summary>Maior RTT (ms) que ainda é rota boa: piso × 2 + folga de acesso.</summary>
    public static double GoodUpperMs(double distanceKm) => FloorMs(distanceKm) * GoodRouteFactor + AccessOverheadMs;

    /// <summary>Quanto do RTT observado passa do esperado para uma rota boa (0 = nada a ganhar com relay).</summary>
    public static double ExcessMs(double observedMs, double distanceKm) => Math.Max(0, observedMs - GoodUpperMs(distanceKm));

    /// <summary>
    /// O RTT observado é impossível para a cidade suposta do servidor (abaixo do piso com 2 ms de tolerância)? Então o servidor
    /// está mais perto do que se pensava, e a cidade suposta está errada.
    /// </summary>
    public static bool ImpossiblyFast(double observedMs, double distanceKm) => observedMs + 2.0 < FloorMs(distanceKm);
}
