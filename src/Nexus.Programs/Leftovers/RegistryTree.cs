using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.WindowsActions.Registry;

namespace Nexus.Programs.Leftovers;

/// <summary>Valor de registro em forma canônica (mesma do <see cref="IRegistryBackend"/>): DWORD/QWORD em decimal, binário em hex,
/// multi-texto separado por NUL. Nome vazio = valor padrão da chave.</summary>
public sealed record RegValueData(string Name, string Kind, string Data);

/// <summary>Árvore completa de uma chave (valores com tipos + subchaves), para exportar antes de remover e recriar no Restore.</summary>
public sealed record RegKeyTree(string Name, IReadOnlyList<RegValueData> Values, IReadOnlyList<RegKeyTree> SubKeys)
{
    public int CountKeys() => 1 + SubKeys.Sum(s => s.CountKeys());
    public int CountValues() => Values.Count + SubKeys.Sum(s => s.CountValues());
}

/// <summary>Leitura de nomes de subchaves (varredura de sobras, sem escrita).</summary>
public interface IRegistryKeyReader
{
    IReadOnlyList<string> SubKeyNames(RegHive hive, string path);
}

/// <summary>Árvore de registro para o handler <c>ProgramLeftover</c>. <c>userSid</c> = HKU\&lt;SID&gt; em vez do HKCU do processo.</summary>
public interface IRegistryTreeBackend : IRegistryKeyReader
{
    bool KeyExists(RegHive hive, string path, string? userSid);
    /// <summary>Exporta a árvore; null se a chave não existe. Lança se passar dos limites (não é sobra típica).</summary>
    RegKeyTree? Export(RegHive hive, string path, string? userSid);
    void DeleteTree(RegHive hive, string path, string? userSid);
    /// <summary>Recria a árvore (cria chaves, grava valores com os tipos exatos).</summary>
    void Import(RegHive hive, string path, RegKeyTree tree, string? userSid);
    /// <summary>Lê um texto de HKLM (ex.: ProfileList para achar a pasta do perfil de um SID).</summary>
    string? ReadMachineString(string path, string name);
}

public static class RegistryTreeLimits
{
    public const int MaxKeys = 5_000;
    public const int MaxValues = 50_000;
}

/// <summary>Registro em memória (testes): chaves com valores, sem diferenciar maiúsculas.</summary>
public sealed class InMemoryRegistryTreeBackend : IRegistryTreeBackend
{
    private readonly Dictionary<string, Dictionary<string, (string Kind, string Data)>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _machineStrings = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegHive hive, string path, string? sid) => $"{(sid is null ? hive.ToString() : "HKU:" + sid)}\\{path.TrimEnd('\\')}";

    public void SetValue(RegHive hive, string path, string name, string kind, string data, string? sid = null)
    {
        EnsureKey(hive, path, sid);
        _keys[K(hive, path, sid)][name] = (kind, data);
    }

    public void EnsureKey(RegHive hive, string path, string? sid = null)
    {
        var parts = path.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
            _keys.TryAdd(K(hive, string.Join('\\', parts.Take(i)), sid), new(StringComparer.OrdinalIgnoreCase));
    }

    public void SetMachineString(string path, string name, string value) => _machineStrings[path + "|" + name] = value;

    public (string Kind, string Data)? GetValue(RegHive hive, string path, string name, string? sid = null) =>
        _keys.TryGetValue(K(hive, path, sid), out var v) && v.TryGetValue(name, out var d) ? d : null;

    public bool KeyExists(RegHive hive, string path, string? userSid) => _keys.ContainsKey(K(hive, path, userSid));

    public IReadOnlyList<string> SubKeyNames(RegHive hive, string path) => SubKeys(K(hive, path, null));

    private List<string> SubKeys(string key)
    {
        var prefix = key + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0)
            .Select(k => k[prefix.Length..]).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RegKeyTree? Export(RegHive hive, string path, string? userSid) => _keys.ContainsKey(K(hive, path, userSid)) ? Build(K(hive, path, userSid)) : null;

    private RegKeyTree Build(string key) => new(
        key[(key.LastIndexOf('\\') + 1)..],
        _keys[key].Select(kv => new RegValueData(kv.Key, kv.Value.Kind, kv.Value.Data)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        SubKeys(key).Select(s => Build(key + "\\" + s)).ToList());

    public void DeleteTree(RegHive hive, string path, string? userSid)
    {
        var key = K(hive, path, userSid);
        foreach (var k in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(k);
    }

    public void Import(RegHive hive, string path, RegKeyTree tree, string? userSid)
    {
        ArgumentNullException.ThrowIfNull(tree);
        EnsureKey(hive, path, userSid);
        foreach (var v in tree.Values) _keys[K(hive, path, userSid)][v.Name] = (v.Kind, v.Data);
        foreach (var s in tree.SubKeys) Import(hive, path + "\\" + s.Name, s, userSid);
    }

    public string? ReadMachineString(string path, string name) => _machineStrings.TryGetValue(path + "|" + name, out var v) ? v : null;
}

/// <summary>
/// Registro real, visão de 64 bits (chaves <c>WOW6432Node</c> aparecem pelo caminho explícito). Com <paramref name="requireUserProfileForHkcu"/>
/// (aplicador como serviço), HKCU sem SID é recusado: seria o perfil do SYSTEM.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryTreeBackend(bool requireUserProfileForHkcu) : IRegistryTreeBackend
{
    private RegistryKey Root(RegHive hive, string? sid, bool writable)
    {
        if (hive == RegHive.HKLM)
        {
            if (sid is not null) throw new InvalidOperationException("Perfil de usuário só se aplica a HKCU.");
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }
        if (sid is null)
        {
            if (requireUserProfileForHkcu) throw new InvalidOperationException("HKCU sem perfil de usuário num processo de serviço: nada foi lido ou escrito.");
            return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        }
        if (!UserHive.IsValidSid(sid)) throw new InvalidOperationException("SID de usuário inválido.");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        return users.OpenSubKey(sid, writable) ?? throw new InvalidOperationException($"Perfil do usuário {sid} não está carregado; nada foi lido ou escrito.");
    }

    public IReadOnlyList<string> SubKeyNames(RegHive hive, string path)
    {
        using var root = Root(hive, null, false);
        using var key = root.OpenSubKey(path, false);
        return key?.GetSubKeyNames() ?? [];
    }

    public bool KeyExists(RegHive hive, string path, string? userSid)
    {
        using var root = Root(hive, userSid, false);
        using var key = root.OpenSubKey(path, false);
        return key is not null;
    }

    public RegKeyTree? Export(RegHive hive, string path, string? userSid)
    {
        using var root = Root(hive, userSid, false);
        using var key = root.OpenSubKey(path, false);
        if (key is null) return null;
        var budget = new int[] { RegistryTreeLimits.MaxKeys, RegistryTreeLimits.MaxValues };
        return Build(key, path[(path.LastIndexOf('\\') + 1)..], budget);
    }

    private static RegKeyTree Build(RegistryKey key, string name, int[] budget)
    {
        if (--budget[0] < 0) throw new InvalidOperationException($"Chave grande demais (mais de {RegistryTreeLimits.MaxKeys} subchaves): não parece sobra de um programa.");
        var values = new List<RegValueData>();
        foreach (var vn in key.GetValueNames())
        {
            if (--budget[1] < 0) throw new InvalidOperationException($"Chave grande demais (mais de {RegistryTreeLimits.MaxValues} valores).");
            var kind = key.GetValueKind(vn);
            var raw = key.GetValue(vn, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(new RegValueData(vn, kind.ToString(), ToCanonical(kind, raw)));
        }
        var subs = new List<RegKeyTree>();
        foreach (var sn in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(sn, false) ?? throw new InvalidOperationException($"Subchave {sn} não pôde ser lida: exportação incompleta, nada será removido.");
            subs.Add(Build(sub, sn, budget));
        }
        return new RegKeyTree(name, values, subs);
    }

    public void DeleteTree(RegHive hive, string path, string? userSid)
    {
        using var root = Root(hive, userSid, true);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public void Import(RegHive hive, string path, RegKeyTree tree, string? userSid)
    {
        ArgumentNullException.ThrowIfNull(tree);
        using var root = Root(hive, userSid, true);
        using var key = root.CreateSubKey(path, true) ?? throw new InvalidOperationException($"Não foi possível criar {hive}\\{path}.");
        Write(key, tree);
    }

    private static void Write(RegistryKey key, RegKeyTree tree)
    {
        foreach (var v in tree.Values)
        {
            var kind = Enum.Parse<RegistryValueKind>(v.Kind, ignoreCase: true);
            key.SetValue(v.Name, FromCanonical(kind, v.Data), kind);
        }
        foreach (var s in tree.SubKeys)
        {
            using var sub = key.CreateSubKey(s.Name, true) ?? throw new InvalidOperationException($"Não foi possível criar a subchave {s.Name}.");
            Write(sub, s);
        }
    }

    public string? ReadMachineString(string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(path, false);
        return key?.GetValue(name) as string; // REG_EXPAND_SZ já expandido.
    }

    internal static string ToCanonical(RegistryValueKind kind, object? raw) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)raw!).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)(long)raw!).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToHexStringLower((byte[])raw!),
        RegistryValueKind.MultiString => string.Join('\u0000', (string[])raw!),
        RegistryValueKind.String or RegistryValueKind.ExpandString => raw?.ToString() ?? string.Empty,
        RegistryValueKind.None => raw is byte[] b ? Convert.ToHexStringLower(b) : "",
        // REG_LINK, listas de recursos etc.: não sabemos recriar exatamente, então a chave inteira não é removida.
        _ => throw new NotSupportedException($"Valor de tipo {kind} não pode ser guardado com exatidão: nada será removido."),
    };

    internal static object FromCanonical(RegistryValueKind kind, string value) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromHexString(value),
        RegistryValueKind.MultiString => value.Length == 0 ? Array.Empty<string>() : value.Split('\u0000'),
        _ => value,
    };
}
