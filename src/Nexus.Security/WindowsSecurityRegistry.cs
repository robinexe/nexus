using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using Nexus.WindowsActions.Registry;

namespace Nexus.Security;

/// <summary>Registro real no nível de chave. HKCU de outro usuário vai para HKU\SID (perfil carregado); nunca cria a raiz do perfil.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityRegistry : ISecurityRegistry
{
    private readonly bool _requireUserProfileForHkcu;

    public WindowsSecurityRegistry(bool requireUserProfileForHkcu = false) => _requireUserProfileForHkcu = requireUserProfileForHkcu;

    private RegistryKey Root(RegKeyRef k)
    {
        if (_requireUserProfileForHkcu && k.Hive == RegHive.HKCU && k.UserSid is null)
            throw new InvalidOperationException("HKCU sem perfil de usuário num processo de serviço: nada foi lido ou escrito.");
        var view = k.View switch { RegView.Registry64 => RegistryView.Registry64, RegView.Registry32 => RegistryView.Registry32, _ => RegistryView.Default };
        if (k.UserSid is null)
            return RegistryKey.OpenBaseKey(k.Hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
        if (k.Hive != RegHive.HKCU || !UserHive.IsValidSid(k.UserSid)) throw new InvalidOperationException("Perfil de usuário inválido.");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
        return users.OpenSubKey(k.UserSid, writable: true) ?? throw new InvalidOperationException($"Perfil {k.UserSid} não está carregado; nada foi lido ou escrito.");
    }

    private RegistryKey? Open(RegKeyRef k, bool writable)
    {
        using var root = Root(k);
        try { return root.OpenSubKey(k.Path, writable); }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) when (!writable) { return null; }
    }

    public bool KeyExists(RegKeyRef key)
    {
        using var k = Open(key, false);
        return k is not null;
    }

    public IReadOnlyList<string> SubKeyNames(RegKeyRef key)
    {
        using var k = Open(key, false);
        return k?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<RegValue> Values(RegKeyRef key)
    {
        using var k = Open(key, false);
        if (k is null) return [];
        var list = new List<RegValue>();
        foreach (var name in k.GetValueNames())
            if (Read(k, name) is { } v) list.Add(v);
        return list;
    }

    public RegValue? Value(RegKeyRef key, string name)
    {
        using var k = Open(key, false);
        return k is null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ? null : Read(k, name);
    }

    private static RegValue? Read(RegistryKey k, string name)
    {
        try
        {
            var kind = k.GetValueKind(name);
            var raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new RegValue(name, kind.ToString(), ToCanonical(kind, raw));
        }
        catch (IOException) { return null; }
    }

    public void SetValue(RegKeyRef key, RegValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var kind = Enum.Parse<RegistryValueKind>(value.Kind, ignoreCase: true);
        if (!RegKinds.Restorable.Contains(value.Kind)) throw new InvalidOperationException($"Tipo {value.Kind} não suportado.");
        using var root = Root(key);
        using var k = root.CreateSubKey(key.Path, writable: true) ?? throw new InvalidOperationException($"Não foi possível abrir {key}.");
        k.SetValue(value.Name, FromCanonical(kind, value.Data), kind);
    }

    public void DeleteValue(RegKeyRef key, string name)
    {
        using var k = Open(key, true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void CreateKey(RegKeyRef key)
    {
        using var root = Root(key);
        using var _ = root.CreateSubKey(key.Path, writable: true);
    }

    public void DeleteKeyTree(RegKeyRef key)
    {
        using var root = Root(key);
        root.DeleteSubKeyTree(key.Path, throwOnMissingSubKey: false);
    }

    internal static string ToCanonical(RegistryValueKind kind, object? raw) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)raw!).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)(long)raw!).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => raw as string ?? "",
        RegistryValueKind.MultiString => JsonSerializer.Serialize(raw as string[] ?? []),
        _ => raw is byte[] b ? Convert.ToHexStringLower(b) : "",
    };

    internal static object FromCanonical(RegistryValueKind kind, string value) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(value, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(value, CultureInfo.InvariantCulture)),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value) ?? [],
        RegistryValueKind.Binary => Convert.FromHexString(value),
        _ => value,
    };
}
