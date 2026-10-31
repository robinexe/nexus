using Nexus.Core;

namespace Nexus.WindowsActions.Registry;

/// <summary>
/// Edita UM token dentro do REG_SZ de HKCU\Software\Microsoft\DirectX\UserGpuPreferences
/// (ex.: "GpuPreference=2;SwapEffectUpgradeEnable=1;"), preservando os demais tokens e a ordem.
/// Alvo: name (caminho do executável ou DirectXUserGlobalSettings), token.
/// </summary>
public sealed class DirectXUserGpuPreferenceHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "DirectXUserGpuPreference";
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static readonly HashSet<string> AllowedTokens = new(StringComparer.Ordinal) { "SwapEffectUpgradeEnable", "GpuPreference", "AutoHDREnable" };
    private readonly IRegistryBackend _reg;

    public DirectXUserGpuPreferenceHandler(IRegistryBackend backend) => _reg = backend;

    public string HandlerKind => Kind;

    private static RegistryTarget TargetOf(ActionInstance a) => new(RegHive.HKCU, KeyPath, a.Target["name"], RegView.Default);

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (!action.Target.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return Applicability.Blocked("Executável não informado.");
        if (!action.Target.TryGetValue("token", out var token) || !AllowedTokens.Contains(token))
            return Applicability.Blocked("Token não permitido.");
        var existing = _reg.Read(TargetOf(action));
        if (existing.ValueExists && !string.Equals(existing.Kind, "String", StringComparison.OrdinalIgnoreCase))
            return Applicability.Blocked($"Valor existente é {existing.Kind}, não REG_SZ; o NEXUS não altera o tipo.");
        if (action.Desired.Exists && (action.Desired.Type != "Token" || action.Desired.Value is null || action.Desired.Value.Contains(';') || action.Desired.Value.Contains('=')))
            return Applicability.Blocked("Valor de token inválido.");
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        if (!action.Target.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) return "Executável não informado.";
        if (!action.Target.TryGetValue("token", out var token) || !AllowedTokens.Contains(token)) return "Token não permitido.";
        if (original.Exists && (original.Type != "Token" || original.Value is null || original.Value.Contains(';') || original.Value.Contains('=')))
            return "Valor original de token inválido.";
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var r = _reg.Read(TargetOf(action));
        var detail = new Dictionary<string, string> { ["valueExisted"] = r.ValueExists ? "true" : "false" };
        if (!r.ValueExists) return StateSnapshot.Absent(detail);
        if (!string.Equals(r.Kind, "String", StringComparison.OrdinalIgnoreCase))
            return new StateSnapshot(true, r.Kind, r.Value, ValueOrigin.Unknown, detail);
        var tokens = TokenString.Parse(r.Value!);
        var token = action.Target["token"];
        return tokens.TryGet(token, out var v) ? StateSnapshot.Of("Token", v, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action) => WriteToken(action, action.Desired, deleteValueIfEmpty: false);

    public void Restore(ActionInstance action, StateSnapshot original) =>
        WriteToken(action, original,
            deleteValueIfEmpty: original.Detail is not null && original.Detail.TryGetValue("valueExisted", out var e) && e == "false");

    private void WriteToken(ActionInstance action, StateSnapshot state, bool deleteValueIfEmpty)
    {
        var t = TargetOf(action);
        var r = _reg.Read(t);
        var tokens = TokenString.Parse(r.ValueExists ? r.Value ?? string.Empty : string.Empty);
        var token = action.Target["token"];
        if (state.Exists) tokens.Set(token, state.Value!);
        else tokens.Remove(token);

        if (tokens.Count == 0 && deleteValueIfEmpty) _reg.DeleteValue(t);
        else _reg.Write(t, "String", tokens.ToString());
    }
}

/// <summary>Lista ordenada de pares chave=valor separados por ';' que preserva tokens desconhecidos.</summary>
public sealed class TokenString
{
    /// <summary>Key nulo = fragmento sem '=' preservado literalmente.</summary>
    private sealed record Item(string? Key, string Value);

    private readonly List<Item> _items = [];

    public int Count => _items.Count;

    public static TokenString Parse(string s)
    {
        var ts = new TokenString();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            ts._items.Add(eq <= 0 ? new Item(null, part) : new Item(part[..eq], part[(eq + 1)..]));
        }
        return ts;
    }

    public bool TryGet(string key, out string value)
    {
        var i = _items.FindIndex(it => it.Key == key);
        value = i >= 0 ? _items[i].Value : string.Empty;
        return i >= 0;
    }

    public void Set(string key, string value)
    {
        var i = _items.FindIndex(it => it.Key == key);
        if (i >= 0) _items[i] = new Item(key, value);
        else _items.Add(new Item(key, value));
    }

    public void Remove(string key) => _items.RemoveAll(it => it.Key == key);

    public override string ToString() =>
        string.Concat(_items.Select(it => it.Key is null ? it.Value + ";" : $"{it.Key}={it.Value};"));
}
