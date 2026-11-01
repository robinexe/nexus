using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Grupos locais pelo SID (NetLocalGroupGetMembers / AddMembers / DelMembers, nível 0 = SID do membro; o nome do grupo é
/// traduzido do SID, então funciona em qualquer idioma do Windows) e a marca do NEXUS em HKLM\SOFTWARE\NEXUS\PerfLogGroup.
/// Escreve: só usado pelo aplicador elevado, dentro do handler <see cref="PerfLogGroupHandler"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsLocalGroupBackend : ILocalGroupBackend
{
    /// <summary>Chave da marca (vista de 64 bits). Valor = SID da conta; dado = "app" ou "installer".</summary>
    public const string MarkerKey = @"SOFTWARE\NEXUS\PerfLogGroup";
    private const int NERR_Success = 0;
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_MEMBER_NOT_IN_ALIAS = 1377;
    private const int ERROR_MEMBER_IN_ALIAS = 1378;

    public static string? CurrentUserSid()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User?.Value;
    }

    private static string GroupName(string groupSid)
    {
        var account = new SecurityIdentifier(groupSid).Translate(typeof(NTAccount)).Value; // "BUILTIN\Usuários do log de desempenho"
        var slash = account.IndexOf('\\', StringComparison.Ordinal);
        return slash >= 0 ? account[(slash + 1)..] : account;
    }

    public bool IsMember(string groupSid, string userSid)
    {
        var target = new SecurityIdentifier(userSid);
        var group = GroupName(groupSid);
        IntPtr resume = IntPtr.Zero;
        while (true)
        {
            var status = NetLocalGroupGetMembers(null, group, 0, out var buffer, -1, out var read, out _, ref resume);
            try
            {
                if (status != NERR_Success && status != ERROR_MORE_DATA) throw new Win32Exception(status, $"Não deu para ler os membros de \"{group}\".");
                for (var i = 0; i < read; i++)
                {
                    var psid = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                    if (psid != IntPtr.Zero && new SecurityIdentifier(psid).Equals(target)) return true;
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) _ = NetApiBufferFree(buffer);
            }
            if (status != ERROR_MORE_DATA) return false;
        }
    }

    public void AddMember(string groupSid, string userSid) => Change(groupSid, userSid, add: true);

    public void RemoveMember(string groupSid, string userSid) => Change(groupSid, userSid, add: false);

    private static void Change(string groupSid, string userSid, bool add)
    {
        var sid = new SecurityIdentifier(userSid);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var group = GroupName(groupSid);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var info = Marshal.AllocHGlobal(IntPtr.Size); // LOCALGROUP_MEMBERS_INFO_0 { PSID lgrmi0_sid; }
        try
        {
            Marshal.WriteIntPtr(info, pin.AddrOfPinnedObject());
            var status = add ? NetLocalGroupAddMembers(null, group, 0, info, 1) : NetLocalGroupDelMembers(null, group, 0, info, 1);
            if (status == NERR_Success || (add && status == ERROR_MEMBER_IN_ALIAS) || (!add && status == ERROR_MEMBER_NOT_IN_ALIAS)) return;
            throw new Win32Exception(status, add ? $"Não deu para adicionar a conta ao grupo \"{group}\"." : $"Não deu para remover a conta do grupo \"{group}\".");
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            pin.Free();
        }
    }

    public void SetMarker(string userSid, string by)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(MarkerKey, writable: true);
        key.SetValue(userSid, by, RegistryValueKind.String);
    }

    public void ClearMarker(string userSid)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(MarkerKey, writable: true);
        key?.DeleteValue(userSid, throwOnMissingValue: false);
    }

    public bool HasMarker(string userSid)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(MarkerKey, writable: false);
        return key?.GetValue(userSid) is string;
    }

    /// <summary>Leitura para a interface: a conta é membro direto do grupo (mesmo que o token atual ainda não tenha o grupo)?</summary>
    public static bool? IsMemberOfPerfLogUsers(string userSid)
    {
        try { return new WindowsLocalGroupBackend().IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, userSid); }
        catch (Exception ex) when (ex is Win32Exception or IdentityNotMappedException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupAddMembers(string? serverName, string groupName, int level, IntPtr buf, int totalEntries);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupDelMembers(string? serverName, string groupName, int level, IntPtr buf, int totalEntries);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);
}
