namespace Nexus.WindowsActions.Registry;

public enum RegHive { HKCU, HKLM }

public enum RegView { Default, Registry64, Registry32 }

public sealed record RegistryTarget(RegHive Hive, string Path, string Name, RegView View)
{
    public override string ToString() => $@"{Hive}\{Path}\{Name} [{View}]";
}

/// <summary>Leitura bruta. <see cref="KeyExists"/> permite remover só a chave que o NEXUS criou.</summary>
public sealed record RegistryRead(bool KeyExists, bool ValueExists, string? Kind, string? Value);

/// <summary>
/// Abstração mínima do registro. Valores trafegam em forma canônica:
/// DWord/QWord em decimal sem sinal, String/ExpandString como texto, Binary em hex minúsculo.
/// </summary>
public interface IRegistryBackend
{
    RegistryRead Read(RegistryTarget target);
    void Write(RegistryTarget target, string kind, string value);
    /// <summary>Remove apenas o valor. Nunca remove a chave.</summary>
    void DeleteValue(RegistryTarget target);
    /// <summary>Remove a chave somente se estiver vazia (sem valores e sem subchaves). Retorna se removeu.</summary>
    bool DeleteKeyIfEmpty(RegHive hive, string path, RegView view);
}

/// <summary>Registro em memória usado nos testes e na simulação de falhas.</summary>
public sealed class InMemoryRegistryBackend : IRegistryBackend
{
    private readonly Dictionary<string, Dictionary<string, (string Kind, string Value)>> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegHive h, string p, RegView v) => $"{h}|{v}|{p.TrimEnd('\\')}";

    public void Seed(RegistryTarget t, string kind, string value) => Write(t, kind, value);
    public void SeedKey(RegHive h, string p, RegView v = RegView.Default) => _keys.TryAdd(K(h, p, v), new(StringComparer.OrdinalIgnoreCase));

    public RegistryRead Read(RegistryTarget target)
    {
        if (!_keys.TryGetValue(K(target.Hive, target.Path, target.View), out var values)) return new(false, false, null, null);
        return values.TryGetValue(target.Name, out var v) ? new(true, true, v.Kind, v.Value) : new(true, false, null, null);
    }

    public void Write(RegistryTarget target, string kind, string value)
    {
        var key = K(target.Hive, target.Path, target.View);
        if (!_keys.TryGetValue(key, out var values)) _keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
        values[target.Name] = (kind, value);
    }

    public void DeleteValue(RegistryTarget target)
    {
        if (_keys.TryGetValue(K(target.Hive, target.Path, target.View), out var values)) values.Remove(target.Name);
    }

    public bool DeleteKeyIfEmpty(RegHive hive, string path, RegView view)
    {
        var key = K(hive, path, view);
        var prefix = key + "\\";
        if (!_keys.TryGetValue(key, out var values) || values.Count > 0) return false;
        if (_keys.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        return _keys.Remove(key);
    }

    public int ValueCount(RegHive h, string p, RegView v = RegView.Default) =>
        _keys.TryGetValue(K(h, p, v), out var values) ? values.Count : -1;
}
