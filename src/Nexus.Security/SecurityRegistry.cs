using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.WindowsActions.Registry;

namespace Nexus.Security;

/// <summary>Valor de registro em forma canônica (DWord/QWord decimal, String/ExpandString texto, MultiString JSON, Binary hex).</summary>
public sealed record RegValue(string Name, string Kind, string Data);

/// <summary>Chave exportada por inteiro (valores + subchaves), em ordem determinística para o hash.</summary>
public sealed record RegKeyExport(string Name, IReadOnlyList<RegValue> Values, IReadOnlyList<RegKeyExport> SubKeys);

/// <summary>Localização de uma chave. <paramref name="UserSid"/>: HKCU de outro usuário (HKU\SID), como no restante do NEXUS.</summary>
public sealed record RegKeyRef(RegHive Hive, string Path, RegView View, string? UserSid = null)
{
    public RegKeyRef Child(string name) => this with { Path = Path.TrimEnd('\\') + "\\" + name };
    public override string ToString() => $@"{(UserSid is null ? Hive.ToString() : "HKU\\" + UserSid)}\{Path}{(View == RegView.Registry32 ? " [32 bits]" : "")}";
}

/// <summary>
/// Registro no nível de chave (o <see cref="IRegistryBackend"/> do núcleo só trata valores). Usado pelo inspetor (leitura) e
/// pelos handlers de remoção com backup completo. Nunca segue nada fora do caminho pedido.
/// </summary>
public interface ISecurityRegistry
{
    bool KeyExists(RegKeyRef key);
    IReadOnlyList<string> SubKeyNames(RegKeyRef key);
    IReadOnlyList<RegValue> Values(RegKeyRef key);
    RegValue? Value(RegKeyRef key, string name);
    void SetValue(RegKeyRef key, RegValue value);
    void DeleteValue(RegKeyRef key, string name);
    void CreateKey(RegKeyRef key);
    /// <summary>Remove a chave e suas subchaves. Só é chamado depois do backup completo verificado.</summary>
    void DeleteKeyTree(RegKeyRef key);
}

/// <summary>Tipos que o NEXUS sabe guardar e recriar exatamente. Outros (REG_NONE, listas de recursos) bloqueiam a remoção.</summary>
public static class RegKinds
{
    public static readonly IReadOnlySet<string> Restorable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "String", "ExpandString", "DWord", "QWord", "MultiString", "Binary" };

    public static bool AllRestorable(RegKeyExport e) => e.Values.All(v => Restorable.Contains(v.Kind)) && e.SubKeys.All(AllRestorable);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static RegKeyExport Export(ISecurityRegistry reg, RegKeyRef key, string name, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(reg);
        if (depth > 16) throw new InvalidOperationException("Chave profunda demais para exportar com segurança.");
        var values = reg.Values(key).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var subs = reg.SubKeyNames(key).Order(StringComparer.OrdinalIgnoreCase).Select(s => Export(reg, key.Child(s), s, depth + 1)).ToList();
        return new RegKeyExport(name, values, subs);
    }

    public static void Import(ISecurityRegistry reg, RegKeyRef key, RegKeyExport export)
    {
        ArgumentNullException.ThrowIfNull(reg);
        ArgumentNullException.ThrowIfNull(export);
        reg.CreateKey(key);
        foreach (var v in export.Values) reg.SetValue(key, v);
        foreach (var s in export.SubKeys) Import(reg, key.Child(s.Name), s);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Registro em memória para testes (chaves hierárquicas por caminho).</summary>
public sealed class InMemorySecurityRegistry : ISecurityRegistry
{
    private readonly Dictionary<string, Dictionary<string, RegValue>> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegKeyRef k) => $"{(k.UserSid is null ? k.Hive.ToString() : "HKU:" + k.UserSid)}|{k.View}|{k.Path.TrimEnd('\\')}";

    public bool KeyExists(RegKeyRef key) => _keys.ContainsKey(K(key));

    public IReadOnlyList<string> SubKeyNames(RegKeyRef key)
    {
        var prefix = K(key) + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<RegValue> Values(RegKeyRef key) => _keys.TryGetValue(K(key), out var v) ? v.Values.ToList() : [];

    public RegValue? Value(RegKeyRef key, string name) => _keys.TryGetValue(K(key), out var v) && v.TryGetValue(name, out var r) ? r : null;

    public void SetValue(RegKeyRef key, RegValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CreateKey(key);
        _keys[K(key)][value.Name] = value;
    }

    public void DeleteValue(RegKeyRef key, string name)
    {
        if (_keys.TryGetValue(K(key), out var v)) v.Remove(name);
    }

    public void CreateKey(RegKeyRef key)
    {
        // Cria também os pais, como o registro real.
        var parts = key.Path.TrimEnd('\\').Split('\\');
        for (var i = 1; i <= parts.Length; i++)
            _keys.TryAdd(K(key with { Path = string.Join('\\', parts.Take(i)) }), new(StringComparer.OrdinalIgnoreCase));
    }

    public void DeleteKeyTree(RegKeyRef key)
    {
        var k = K(key);
        foreach (var x in _keys.Keys.Where(x => x.Equals(k, StringComparison.OrdinalIgnoreCase) || x.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(x);
    }

    /// <summary>Atalho dos testes: grava um valor de texto.</summary>
    public InMemorySecurityRegistry Str(RegKeyRef key, string name, string data, string kind = "String")
    {
        SetValue(key, new RegValue(name, kind, data));
        return this;
    }
}
