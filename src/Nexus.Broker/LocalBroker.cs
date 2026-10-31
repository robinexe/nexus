using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nexus.Broker;

/// <summary>Nível do broker sob demanda: com a conta do usuário (sem aviso) ou elevado (aviso do Windows, UAC).</summary>
public enum BrokerLevel { User, Admin }

/// <summary>
/// Broker sob demanda para aplicar neste PC sem instalar nada. A interface continua sem caminho de execução: ela só inicia o
/// processo do broker (o mesmo motor, catálogo, portão, trava e diário) e conversa pelo pipe.
/// <list type="bullet">
/// <item>Usuário: <c>NexusBroker.v1.u.&lt;SID&gt;</c>, diário em %LocalAppData%\NEXUS, pipe só do usuário (dono conferido).</item>
/// <item>Administrador: primeiro tenta o serviço instalado; senão inicia o broker elevado (UAC) em <c>NexusBroker.v1.a.&lt;SID&gt;</c>,
/// diário em %ProgramData%\NEXUS; o cliente exige dono SYSTEM/Administradores.</item>
/// </list>
/// O broker sob demanda encerra sozinho quando a interface fecha ou depois de 10 minutos sem pedidos.
/// </summary>
public static class LocalBroker
{
    public const int IdleExitSeconds = 600;

    public static string PipeName(BrokerLevel level, string userSid) =>
        $"{BrokerPipeServer.DefaultPipeName}.{(level == BrokerLevel.User ? "u" : "a")}.{userSid}";

    public static string JournalPath(BrokerLevel level) => Path.Combine(
        Environment.GetFolderPath(level == BrokerLevel.User ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.CommonApplicationData),
        "NEXUS", "journal.db");

    /// <summary>Executável do broker ao lado da interface (nexus-broker.exe), ou a dll via dotnet.</summary>
    public static (string File, IReadOnlyList<string> PrefixArgs) Executable(string directory)
    {
        var exe = Path.Combine(directory, OperatingSystem.IsWindows() ? "nexus-broker.exe" : "nexus-broker");
        if (File.Exists(exe)) return (exe, []);
        return ("dotnet", [Path.Combine(directory, "nexus-broker.dll")]);
    }

    public static IReadOnlyList<string> Arguments(BrokerLevel level, string userSid, int parentPid) =>
    [
        "--pipe", PipeName(level, userSid),
        "--journal", JournalPath(level),
        "--allow-sid", userSid,
        "--allow-host",
        "--idle-exit", IdleExitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--parent-pid", parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// Conecta ao broker do nível pedido, iniciando-o se preciso. Exceções: <see cref="OperationCanceledException"/> quando o
    /// usuário recusa o aviso do Windows; <see cref="TimeoutException"/> se o broker não responder.
    /// </summary>
    public static BrokerClient Connect(BrokerLevel level, string brokerDirectory, string userSid, Action<string>? progress = null)
    {
        if (level == BrokerLevel.Admin && TryConnect(BrokerPipeServer.DefaultPipeName, BrokerServerKind.Service, 200) is { } service)
            return service; // serviço instalado
        var kind = level == BrokerLevel.User ? BrokerServerKind.SameUser : BrokerServerKind.Service;
        var pipe = PipeName(level, userSid);
        if (TryConnect(pipe, kind, 300) is { } running) return running;

        progress?.Invoke(level == BrokerLevel.Admin ? "Confirme o aviso do Windows para o NEXUS aplicar como administrador…" : "Iniciando o aplicador do NEXUS…");
        Start(level, brokerDirectory, userSid);
        var deadline = DateTime.UtcNow + (level == BrokerLevel.Admin ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (TryConnect(pipe, kind, 500) is { } c) return c;
            Thread.Sleep(250);
        }
        throw new TimeoutException("O aplicador do NEXUS não respondeu a tempo.");
    }

    private static BrokerClient? TryConnect(string pipe, BrokerServerKind kind, int timeoutMs)
    {
        try { return BrokerClient.Connect(pipe, timeoutMs, kind); }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
    }

    private static void Start(BrokerLevel level, string brokerDirectory, string userSid)
    {
        // Modo portátil: numa pasta que contas comuns alteram (Downloads, Área de Trabalho, D:\ com a ACL padrão), o aplicador
        // ELEVADO rodaria arquivos que qualquer programa com a conta do usuário pode trocar antes do aviso do Windows. A checagem
        // fica AQUI, no cliente, antes de pedir a elevação: depois de elevado, um binário trocado não se denunciaria.
        // O serviço instalado (tentado antes, em Connect) não passa por aqui. Desenvolvimento/CI (pasta bin gravável):
        // NEXUS_ALLOW_WRITABLE_BROKER_DIR=1.
        if (level == BrokerLevel.Admin && OperatingSystem.IsWindows() &&
            Environment.GetEnvironmentVariable("NEXUS_ALLOW_WRITABLE_BROKER_DIR") != "1" &&
            UnsafeBrokerFolder(brokerDirectory) is { } why)
            throw new OperationCanceledException(
                "Ações de administrador desligadas nesta cópia do NEXUS: " + why + ". Programas com a sua conta poderiam trocar os " +
                "arquivos que rodariam como administrador. Use o instalador (Arquivos de Programas) ou mova a pasta para um lugar que só " +
                "administradores alteram. Nada foi alterado.");
        var (file, prefix) = Executable(brokerDirectory);
        var args = prefix.Concat(Arguments(level, userSid, Environment.ProcessId)).ToList();
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = level == BrokerLevel.Admin, // "runas" exige ShellExecute
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = brokerDirectory,
        };
        if (level == BrokerLevel.Admin)
        {
            psi.Verb = "runas";
            psi.Arguments = string.Join(' ', args.Select(Quote));
        }
        else
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        try
        {
            using var p = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: usuário recusou o UAC
        {
            throw new OperationCanceledException("Você recusou o aviso do Windows. Nada foi alterado.", ex);
        }
    }

    internal static string Quote(string a) => a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0 ? a : "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // Mesma regra de Nexus.WindowsActions.AdminOnlyDirectory.Evaluate e de tools/packaging/Test-NexusFolderSafety.ps1.
    private const int WriteLike = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;
    private const int DeleteChild = 0x40 | 0x10000000;
    private static readonly string[] PrivilegedSids =
        ["S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];

    /// <summary>
    /// null se só administradores alteram a pasta do aplicador, os .exe/.dll/.json dela e o catálogo, e nenhuma pasta acima
    /// permite trocá-la (excluir subpastas); senão, o motivo em português simples.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string? UnsafeBrokerFolder(string directory)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(directory));
            if (dir.LinkTarget is not null || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return "a pasta é um link ou junção";
            if (AclProblem(dir.GetAccessControl(), WriteLike, checkOwner: true) is { } p) return $"{dir.FullName}: {p}";
            foreach (var f in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                var ext = f.Extension.ToUpperInvariant();
                if (ext is not (".EXE" or ".DLL" or ".JSON")) continue;
                if (AclProblem(f.GetAccessControl(), WriteLike, checkOwner: true) is { } fp) return $"{f.Name}: {fp}";
            }
            var catalog = new DirectoryInfo(Path.Combine(dir.FullName, "catalog"));
            if (catalog.Exists && AclProblem(catalog.GetAccessControl(), WriteLike, checkOwner: true) is { } cp) return $"catalog: {cp}";
            for (var parent = dir.Parent; parent is not null; parent = parent.Parent)
                if (AclProblem(parent.GetAccessControl(), DeleteChild, checkOwner: false) is { } pp) return $"{parent.FullName}: {pp}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"não foi possível conferir as permissões da pasta ({ex.Message})";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? AclProblem(FileSystemSecurity security, int mask, bool checkOwner)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (checkOwner && (owner is null || !PrivilegedSids.Contains(owner))) return $"o dono ({owner ?? "desconhecido"}) não é Administradores/SYSTEM";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var sid = rule.IdentityReference.Value;
            if (PrivilegedSids.Contains(sid) || sid == "S-1-3-0") continue; // CREATOR OWNER só vale para o que for criado dentro
            if (((int)rule.FileSystemRights & mask) != 0) return $"a conta {sid} pode alterar arquivos ali";
        }
        return null;
    }
}
