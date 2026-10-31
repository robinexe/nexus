using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.WindowsActions.Registry;

/// <summary>Backend real. Usa a visão 32/64 bits pedida explicitamente; nunca apaga árvores de chaves.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryBackend : IRegistryBackend
{
    private readonly bool _requireUserProfileForHkcu;

    public WindowsRegistryBackend() { }

    /// <param name="requireUserProfileForHkcu">Processo rodando como LocalSystem: HKCU sem SID de usuário seria o perfil do
    /// SYSTEM, então é recusado (segunda barreira do achado B10, além do broker).</param>
    public WindowsRegistryBackend(bool requireUserProfileForHkcu) => _requireUserProfileForHkcu = requireUserProfileForHkcu;

    private RegistryKey Base(RegHive hive, RegView view, string? userSid = null)
    {
        if (_requireUserProfileForHkcu && hive == RegHive.HKCU && userSid is null)
            throw new InvalidOperationException("HKCU sem perfil de usuário num processo de serviço: seria o perfil do SYSTEM; nada foi lido ou escrito.");
        var v = view switch
        {
            RegView.Registry64 => RegistryView.Registry64,
            RegView.Registry32 => RegistryView.Registry32,
            _ => RegistryView.Default,
        };
        if (userSid is null)
            return RegistryKey.OpenBaseKey(hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, v);

        // HKCU de outro usuário (broker como serviço): HKU\<SID>. Só existe com o perfil carregado (usuário com sessão).
        // Nunca cria a raiz: se o perfil não estiver carregado, falha em vez de escrever num lugar que o Windows ignora.
        if (hive != RegHive.HKCU || !UserHive.IsValidSid(userSid))
            throw new InvalidOperationException("Perfil de usuário inválido para este alvo.");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, v);
        return users.OpenSubKey(userSid, writable: true)
               ?? throw new InvalidOperationException($"Perfil do usuário {userSid} não está carregado (sem sessão aberta); nada foi lido ou escrito.");
    }

    public RegistryRead Read(RegistryTarget target)
    {
        using var root = Base(target.Hive, target.View, target.UserSid);
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
        using var root = Base(target.Hive, target.View, target.UserSid);
        using var key = root.CreateSubKey(target.Path, writable: true)
                        ?? throw new InvalidOperationException($"Não foi possível abrir/criar {target.Hive}\\{target.Path}");
        key.SetValue(target.Name, FromCanonical(k, value), k);
    }

    public void DeleteValue(RegistryTarget target)
    {
        using var root = Base(target.Hive, target.View, target.UserSid);
        using var key = root.OpenSubKey(target.Path, writable: true);
        key?.DeleteValue(target.Name, throwOnMissingValue: false);
    }

    public bool DeleteKeyIfEmpty(RegHive hive, string path, RegView view, string? userSid = null)
    {
        using var root = Base(hive, view, userSid);
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
