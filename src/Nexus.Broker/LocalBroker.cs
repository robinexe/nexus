using System.ComponentModel;
using System.Diagnostics;

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
}
