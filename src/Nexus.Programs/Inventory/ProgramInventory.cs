using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Programs.Inventory;

/// <summary>De onde veio a entrada: chave Uninstall de 64 bits, de 32 bits (WOW6432Node), do usuário, ou app da Store.</summary>
public enum InstallScope { Machine64, Machine32, User, Store }

/// <summary>Uma subchave de <c>...\CurrentVersion\Uninstall</c> como foi lida (valores em texto; DWORD em decimal).</summary>
public sealed record UninstallEntryRaw(InstallScope Scope, string KeyName, IReadOnlyDictionary<string, string> Values);

/// <summary>Fonte somente leitura das chaves Uninstall (Windows real ou falsa nos testes).</summary>
public interface IUninstallRegistrySource
{
    IReadOnlyList<UninstallEntryRaw> ReadAll();
}

/// <summary>Programa instalado, como o Windows o registra para "Aplicativos instalados".</summary>
public sealed record InstalledProgram(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    long? SizeBytes,
    DateOnly? InstallDate,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    string? DisplayIcon,
    InstallScope Scope,
    string KeyName,
    bool IsMsi,
    bool NoRemove)
{
    public string ScopeText => Scope switch
    {
        InstallScope.Machine64 => "Todos os usuários (64 bits)",
        InstallScope.Machine32 => "Todos os usuários (32 bits)",
        InstallScope.User => "Só este usuário",
        _ => "Microsoft Store",
    };

    public bool IsMachineWide => Scope is InstallScope.Machine64 or InstallScope.Machine32;
}

public enum ProgramSort { Name, SizeDescending, DateDescending, Publisher }

/// <summary>
/// Monta a lista de programas a partir das chaves Uninstall com os mesmos filtros que "Aplicativos instalados" usa:
/// some quem é componente do sistema (<c>SystemComponent=1</c>), atualização/filho de outro produto (<c>ParentKeyName</c>,
/// <c>ReleaseType</c> de atualização), sem nome, ou sem desinstalador. Duplicatas (mesmo nome e versão em visões diferentes)
/// viram uma só. Tudo puro: testável com uma fonte falsa.
/// </summary>
public static partial class ProgramInventory
{
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GuidKey();

    [GeneratedRegex(@"^KB\d{6,8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KbKey();

    private static readonly string[] UpdateReleaseTypes = ["Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup"];

    public static IReadOnlyList<InstalledProgram> Build(IEnumerable<UninstallEntryRaw> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<InstalledProgram>();
        foreach (var e in entries)
        {
            if (ToProgram(e) is { } p) list.Add(p);
        }
        // Mesmo produto registrado nas duas visões (ou máquina e usuário): fica o primeiro (64 bits > 32 bits > usuário).
        return list
            .OrderBy(p => p.Scope)
            .GroupBy(p => (p.Name.ToUpperInvariant(), (p.Version ?? "").ToUpperInvariant()))
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Converte uma entrada, ou null quando ela não aparece para o usuário (filtros acima).</summary>
    public static InstalledProgram? ToProgram(UninstallEntryRaw e)
    {
        ArgumentNullException.ThrowIfNull(e);
        string? V(string name) => e.Values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var name = V("DisplayName");
        if (name is null) return null;
        if (V("SystemComponent") == "1") return null;
        if (V("ParentKeyName") is not null || V("ParentDisplayName") is not null && V("ReleaseType") is not null) return null;
        if (V("ReleaseType") is { } rt && UpdateReleaseTypes.Contains(rt, StringComparer.OrdinalIgnoreCase)) return null;
        if (KbKey().IsMatch(e.KeyName)) return null;
        var uninstall = V("UninstallString");
        if (uninstall is null) return null; // sem desinstalador: "Aplicativos instalados" também não mostra.

        long? size = null;
        if (V("EstimatedSize") is { } es && long.TryParse(es, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb > 0)
            size = kb * 1024; // EstimatedSize é DWORD em KB.

        return new InstalledProgram(
            Id: $"{e.Scope}|{e.KeyName}",
            Name: name,
            Publisher: V("Publisher"),
            Version: V("DisplayVersion"),
            SizeBytes: size,
            InstallDate: ParseInstallDate(V("InstallDate")),
            InstallLocation: CleanPath(V("InstallLocation")),
            UninstallString: uninstall,
            QuietUninstallString: V("QuietUninstallString"),
            DisplayIcon: V("DisplayIcon"),
            Scope: e.Scope,
            KeyName: e.KeyName,
            IsMsi: V("WindowsInstaller") == "1" || GuidKey().IsMatch(e.KeyName),
            NoRemove: V("NoRemove") == "1");
    }

    /// <summary>InstallDate é texto "yyyyMMdd" (às vezes com separadores, ou lixo). Data impossível = desconhecida.</summary>
    public static DateOnly? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 8 && DateOnly.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
            d.Year >= 1995 && d.Year <= 2100)
            return d;
        return null;
    }

    /// <summary>Tira aspas e barra final de um caminho registrado (InstallLocation vem em todos os formatos).</summary>
    public static string? CleanPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = raw.Trim().Trim('"').Trim();
        while (p.Length > 3 && (p.EndsWith('\\') || p.EndsWith('/'))) p = p[..^1];
        return p.Length == 0 ? null : p;
    }

    /// <summary>O programa ainda está na lista (mesma chave, ou mesmo nome e versão registrados em outra visão).</summary>
    public static bool Contains(IEnumerable<InstalledProgram> installed, InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(program);
        return installed.Any(x => x.Id == program.Id ||
            (string.Equals(x.Name, program.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version, program.Version, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<InstalledProgram> Filter(IEnumerable<InstalledProgram> programs, string? query)
    {
        ArgumentNullException.ThrowIfNull(programs);
        if (string.IsNullOrWhiteSpace(query)) return programs.ToList();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return programs.Where(p => terms.All(t =>
            p.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase) ||
            (p.Publisher?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false))).ToList();
    }

    public static IReadOnlyList<InstalledProgram> Sort(IEnumerable<InstalledProgram> programs, ProgramSort sort)
    {
        ArgumentNullException.ThrowIfNull(programs);
        return sort switch
        {
            ProgramSort.SizeDescending => programs.OrderByDescending(p => p.SizeBytes ?? -1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            ProgramSort.DateDescending => programs.OrderByDescending(p => p.InstallDate ?? DateOnly.MinValue).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            ProgramSort.Publisher => programs.OrderBy(p => p.Publisher ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        };
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is not { } b || b <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("pt-BR")) + " " + units[i];
    }
}
