using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Core;

namespace Nexus.SystemTools.Wifi;

/// <summary>Um perfil num adaptador Wi-Fi (o mesmo nome pode existir em mais de um adaptador).</summary>
/// <param name="UserOnly">Perfil só do usuário (WLAN_PROFILE_USER), não de todos os usuários.</param>
/// <param name="GroupPolicy">Perfil de política de grupo (WLAN_PROFILE_GROUP_POLICY).</param>
public sealed record WifiProfileRef(Guid Interface, string Name, bool UserOnly, bool GroupPolicy);

public sealed class WifiUnavailableException(string message) : Exception(message);

public interface IWifiBackend
{
    /// <summary>Onde o perfil existe. Lança <see cref="WifiUnavailableException"/> sem serviço/adaptador Wi-Fi.</summary>
    IReadOnlyList<WifiProfileRef> Find(string profileName);

    /// <summary>Adaptadores Wi-Fi presentes.</summary>
    IReadOnlyList<Guid> Interfaces();

    /// <summary>XML do perfil com a chave em texto (exige administrador para perfis de todos os usuários).</summary>
    string ExportXml(Guid iface, string profileName);

    void Delete(Guid iface, string profileName);

    void Import(Guid iface, string xml, bool userOnly);
}

/// <summary>Protege o backup (DPAPI). O escopo vai junto do arquivo para o relatório; a leitura não depende dele.</summary>
public interface IBackupProtector
{
    string Scope { get; }
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Remove um perfil Wi-Fi salvo, com backup. Detect: "presente"/"removido" (em qualquer adaptador). Apply: exporta o XML de
/// cada adaptador (com a chave, protegido por DPAPI em DataDirectory\backup\wifi), confere o backup e só então apaga.
/// Restore: reimporta o XML no(s) adaptador(es) onde estava. Perfis de política de grupo nunca são removidos.
/// </summary>
public sealed class WifiProfileHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "SystemWifiProfile";
    public const string StateType = "WifiProfile";
    public const int MaxNameLength = 255;

    private readonly IWifiBackend _wifi;
    private readonly IBackupProtector _protector;
    private readonly string _backupDir;
    private readonly bool _otherAccount;
    private readonly Action<string>? _restrictBackupDirectory;

    /// <param name="restrictBackupDirectory">Chamado com a pasta dos backups ANTES de gravar qualquer arquivo nela. No aplicador como
    /// serviço o backup usa DPAPI da máquina (qualquer conta local consegue decifrar), então a pasta precisa ficar só para
    /// SYSTEM/Administradores; senão um usuário comum leria as senhas de Wi-Fi de todos os usuários.</param>
    public WifiProfileHandler(IWifiBackend wifi, IBackupProtector protector, string dataDirectory, bool otherAccount, Action<string>? restrictBackupDirectory = null)
    {
        _restrictBackupDirectory = restrictBackupDirectory;
        _wifi = wifi ?? throw new ArgumentNullException(nameof(wifi));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _backupDir = Path.Combine(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)), "backup", "wifi");
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && !Text.HasControl(name) && name == name.Trim();

    public string BackupPath(string profileName) => Path.Combine(_backupDir, Text.ShortHash(profileName) + ".wifi.json");

    private static string NameOf(ActionInstance a) => a.Target.TryGetValue("profileName", out var n) ? n : "";

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var name = NameOf(action);
        if (!IsValidName(name)) return Applicability.Blocked("Nome de perfil Wi-Fi inválido.");
        if (action.Desired.Type != StateType || action.Desired.Value != PresenceState.Removed) return Applicability.Blocked("Valor desejado inválido.");
        IReadOnlyList<WifiProfileRef> refs;
        try { refs = _wifi.Find(name); }
        catch (WifiUnavailableException ex) { return Applicability.NotApplicable(ex.Message); }
        if (refs.Count == 0)
            return Applicability.NotApplicable(_otherAccount
                ? $"Perfil '{name}' não encontrado. Perfis salvos só para um usuário não são visíveis ao serviço do NEXUS."
                : $"Perfil '{name}' não existe neste PC.");
        if (refs.Any(r => r.GroupPolicy)) return Applicability.Blocked("Perfil definido por política de grupo (organização): o NEXUS não remove.");
        if (_otherAccount && refs.Any(r => r.UserOnly))
            return Applicability.Blocked("Perfil salvo só para um usuário: o aplicador roda com outra conta e não pode guardá-lo e restaurá-lo com segurança.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IReadOnlyList<WifiProfileRef> refs;
        try { refs = _wifi.Find(NameOf(action)); }
        catch (WifiUnavailableException) { refs = []; }
        return PresenceState.Of(StateType, refs.Count > 0);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var name = NameOf(action);
        if (!IsValidName(name)) throw new InvalidOperationException("Nome de perfil inválido.");
        var refs = _wifi.Find(name);
        if (refs.Any(r => r.GroupPolicy)) throw new InvalidOperationException("Perfil de política de grupo: não removido.");

        var backup = new WifiBackup(1, name, _protector.Scope, DateTime.UtcNow,
            refs.Select(r => new WifiBackupEntry(r.Interface, r.UserOnly, _wifi.ExportXml(r.Interface, name))).ToList());
        if (backup.Entries.Count == 0) throw new InvalidOperationException("Perfil não encontrado no momento de aplicar.");
        if (backup.Entries.Any(e => string.IsNullOrWhiteSpace(e.Xml) || !e.Xml.Contains("<WLANProfile", StringComparison.Ordinal)))
            throw new InvalidOperationException("O Windows não devolveu o perfil completo: nada foi removido.");

        WriteBackup(backup);
        // Confere o backup gravado antes de apagar qualquer coisa.
        var check = ReadBackup(name) ?? throw new InvalidOperationException("Backup não pôde ser relido: nada foi removido.");
        if (check.Entries.Count != backup.Entries.Count) throw new InvalidOperationException("Backup incompleto: nada foi removido.");

        foreach (var r in refs) _wifi.Delete(r.Interface, name);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var name = NameOf(action);
        if (original.Value != PresenceState.Present) return; // não existia antes: nada a recriar
        var backup = ReadBackup(name) ?? throw new InvalidOperationException($"Backup do perfil '{name}' não encontrado em {_backupDir}.");
        var present = _wifi.Find(name).Select(r => r.Interface).ToHashSet();
        var interfaces = _wifi.Interfaces();
        if (interfaces.Count == 0) throw new InvalidOperationException("Nenhum adaptador Wi-Fi presente para reimportar o perfil.");
        foreach (var e in backup.Entries)
        {
            // Adaptador trocado/removido: reimporta no primeiro presente (o perfil é o mesmo; o adaptador muda).
            var target = interfaces.Contains(e.Interface) ? e.Interface : interfaces[0];
            if (present.Contains(target)) continue; // já existe (não sobrescreve um perfil novo do usuário)
            _wifi.Import(target, e.Xml, e.UserOnly);
            present.Add(target);
        }
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var name = NameOf(action);
        if (!IsValidName(name)) return "Nome de perfil inválido.";
        if (original.Type != StateType || (original.Value != PresenceState.Present && original.Value != PresenceState.Removed)) return "Original inválido.";
        if (original.Value == PresenceState.Present && !File.Exists(BackupPath(name))) return "Backup do perfil não encontrado; nada foi escrito.";
        return null;
    }

    // ------------------------------------------------------------------ backup

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private void WriteBackup(WifiBackup backup)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(backup, Json);
        var envelope = new WifiBackupFile("nexus.wifi-backup/1", _protector.Scope, Convert.ToBase64String(_protector.Protect(plain)));
        Directory.CreateDirectory(_backupDir);
        _restrictBackupDirectory?.Invoke(_backupDir);
        Text.WriteAtomic(BackupPath(backup.ProfileName), JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
    }

    internal WifiBackup? ReadBackup(string name)
    {
        var path = BackupPath(name);
        if (!File.Exists(path)) return null;
        var envelope = JsonSerializer.Deserialize<WifiBackupFile>(File.ReadAllBytes(path), Json);
        if (envelope is null || envelope.Format != "nexus.wifi-backup/1") return null;
        var backup = JsonSerializer.Deserialize<WifiBackup>(_protector.Unprotect(Convert.FromBase64String(envelope.Data)), Json);
        return backup is not null && backup.ProfileName == name ? backup : null;
    }
}

internal sealed record WifiBackupFile(string Format, string Scope, string Data);
internal sealed record WifiBackupEntry(Guid Interface, bool UserOnly, string Xml);
internal sealed record WifiBackup(int Version, string ProfileName, string Scope, DateTime CreatedUtc, IReadOnlyList<WifiBackupEntry> Entries);

/// <summary>DPAPI (crypt32). Escopo da máquina quando o aplicador roda como outra conta (serviço); senão, o do usuário.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class DpapiProtector(bool machineScope) : IBackupProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NEXUS.SystemTools.wifi.v1");
    private const int UiForbidden = 0x1, LocalMachine = 0x4;

    public string Scope => machineScope ? "LocalMachine" : "CurrentUser";

    public byte[] Protect(byte[] data) => Transform(data, protect: true);
    public byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private unsafe byte[] Transform(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        fixed (byte* pData = data)
        fixed (byte* pEntropy = Entropy)
        {
            var input = new DataBlob { Size = data.Length, Data = (IntPtr)pData };
            var entropy = new DataBlob { Size = Entropy.Length, Data = (IntPtr)pEntropy };
            DataBlob output;
            var flags = UiForbidden | (protect && machineScope ? LocalMachine : 0);
            var ok = protect
                ? CryptProtectData(&input, null, &entropy, IntPtr.Zero, IntPtr.Zero, flags, &output)
                : CryptUnprotectData(&input, IntPtr.Zero, &entropy, IntPtr.Zero, IntPtr.Zero, flags, &output);
            if (!ok) throw new InvalidOperationException($"DPAPI falhou (código {Marshal.GetLastPInvokeError()}).");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptProtectData(DataBlob* input, string? description, DataBlob* entropy, IntPtr reserved, IntPtr prompt, int flags, DataBlob* output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptUnprotectData(DataBlob* input, IntPtr description, DataBlob* entropy, IntPtr reserved, IntPtr prompt, int flags, DataBlob* output);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}
