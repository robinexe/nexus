using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.WindowsActions.Registry;

/// <summary>Backend real. Usa a visão 32/64 bits pedida explicitamente; nunca apaga árvores de chaves.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryBackend : IRegistryBackend
{
    private static RegistryKey Base(RegHive hive, RegView view)
    {
        var v = view switch
        {
            RegView.Registry64 => RegistryView.Registry64,
            RegView.Registry32 => RegistryView.Registry32,
            _ => RegistryView.Default,
        };
        return RegistryKey.OpenBaseKey(hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, v);
    }

    public RegistryRead Read(RegistryTarget target)
    {
        using var root = Base(target.Hive, target.View);
        using var key = root.OpenSubKey(target.Path, writable: false);
        if (key is null) return new(false, false, null, null);
        if (!key.GetValueNames().Contains(target.Name, StringComparer.OrdinalIgnoreCase)) return new(true, false, null, null);

        var kind = key.GetValueKind(target.Name);
        var raw = key.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new(true, true, kind.ToString(), ToCanonical(kind, raw));
    }

    public void Write(RegistryTarget target, string kind, string value)
    {
        var k = Enum.Parse<RegistryValueKind>(kind, ignoreCase: true);
        using var root = Base(target.Hive, target.View);
        using var key = root.CreateSubKey(target.Path, writable: true)
                        ?? throw new InvalidOperationException($"Não foi possível abrir/criar {target.Hive}\\{target.Path}");
        key.SetValue(target.Name, FromCanonical(k, value), k);
    }

    public void DeleteValue(RegistryTarget target)
    {
        using var root = Base(target.Hive, target.View);
        using var key = root.OpenSubKey(target.Path, writable: true);
        key?.DeleteValue(target.Name, throwOnMissingValue: false);
    }

    public bool DeleteKeyIfEmpty(RegHive hive, string path, RegView view)
    {
        using var root = Base(hive, view);
        using (var key = root.OpenSubKey(path, writable: false))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return false;
        }
        root.DeleteSubKey(path, throwOnMissingSubKey: false); // DeleteSubKey falha se houver subchaves: proteção adicional.
        return true;
    }

    internal static string ToCanonical(RegistryValueKind kind, object? raw) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)raw!).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)(long)raw!).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToHexStringLower((byte[])raw!),
        RegistryValueKind.MultiString => string.Join('\u0000', (string[])raw!),
        RegistryValueKind.String or RegistryValueKind.ExpandString => raw?.ToString() ?? string.Empty,
        // REG_NONE e tipos desconhecidos: bytes crus em hex, para backup e restauração exatos.
        _ => raw is byte[] b ? Convert.ToHexStringLower(b) : throw new NotSupportedException($"Tipo de registro não suportado: {kind}"),
    };

    internal static object FromCanonical(RegistryValueKind kind, string value) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryValueKind.Binary => Convert.FromHexString(value),
        RegistryValueKind.MultiString => value.Length == 0 ? Array.Empty<string>() : value.Split('\u0000'),
        RegistryValueKind.None => Convert.FromHexString(value),
        _ => value,
    };
}
