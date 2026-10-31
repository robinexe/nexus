using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.DiskTools.Analysis;

/// <summary>
/// Nomes dos jogos instalados, lidos (somente leitura) dos manifestos das lojas: Steam (<c>steamapps\appmanifest_*.acf</c>,
/// campos "name" e "installdir") e Epic (<c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item</c>, JSON com
/// "DisplayName" e "InstallLocation"). Sem manifesto, o nome da pasta é usado.
/// </summary>
public static partial class GameLibraries
{
    [GeneratedRegex("\"(?<k>name|installdir|appid)\"\\s+\"(?<v>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex AcfField();

    /// <summary>Lê um appmanifest da Steam. Retorna (pasta de instalação, nome, appid) ou null.</summary>
    public static (string InstallDir, string Name, string? AppId)? ParseSteamManifest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? name = null, dir = null, appId = null;
        foreach (Match m in AcfField().Matches(text))
        {
            var k = m.Groups["k"].Value.ToLowerInvariant();
            var v = m.Groups["v"].Value;
            if (k == "name" && name is null) name = v;
            else if (k == "installdir" && dir is null) dir = v;
            else if (k == "appid" && appId is null) appId = v;
        }
        return name is { Length: > 0 } && dir is { Length: > 0 } ? (dir, name, appId) : null;
    }

    /// <summary>Lê um manifesto .item da Epic. Retorna (pasta de instalação completa, nome) ou null.</summary>
    public static (string InstallLocation, string Name)? ParseEpicManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var name = root.TryGetProperty("DisplayName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var loc = root.TryGetProperty("InstallLocation", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            return name is { Length: > 0 } && loc is { Length: > 0 } ? (loc, name) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Nomes da Steam para uma pasta "steamapps": installdir → nome. Erros de leitura são ignorados.</summary>
    public static IReadOnlyDictionary<string, string> ReadSteamNames(string steamappsDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(steamappsDirectory, "appmanifest_*.acf").Take(2000))
            {
                try
                {
                    if (new FileInfo(file).Length > 256 * 1024) continue;
                    if (ParseSteamManifest(File.ReadAllText(file)) is { } m) map[m.InstallDir] = m.Name;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return map;
    }

    /// <summary>Nomes da Epic: pasta de instalação (caminho completo) → nome.</summary>
    public static IReadOnlyDictionary<string, string> ReadEpicNames(string manifestsDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(manifestsDirectory)) return map;
            foreach (var file in Directory.EnumerateFiles(manifestsDirectory, "*.item").Take(2000))
            {
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    if (ParseEpicManifest(File.ReadAllText(file)) is { } m)
                        map[m.InstallLocation.TrimEnd('\\', '/')] = m.Name;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return map;
    }
}
