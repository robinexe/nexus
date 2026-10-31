using Nexus.WindowsActions.Registry;

namespace Nexus.Boot.Inventory;

/// <summary>Valor de registro lido (forma canônica de <see cref="IRegistryBackend"/>: Binary em hex minúsculo).</summary>
public sealed record RegValue(string Name, string Kind, string Data);

/// <summary>Leitura de registro com enumeração (o <see cref="IRegistryBackend"/> do aplicador só lê valores nomeados).</summary>
public interface IBootRegistryReader
{
    /// <summary>Valores da chave; vazio se a chave não existe ou não pode ser lida.</summary>
    IReadOnlyList<RegValue> Values(RegHive hive, string path, RegView view);
}

/// <summary>De onde veio o nome do editor de um arquivo.</summary>
public enum PublisherSource
{
    /// <summary>Assinatura Authenticode embutida, válida (verificada sem rede).</summary>
    Signature,
    /// <summary>Há assinatura, mas ela não foi validada (expirada, adulterada, raiz não confiável ou revogação não verificável).</summary>
    SignatureNotTrusted,
    /// <summary>Sem assinatura embutida: nome da empresa nas informações de versão do arquivo (qualquer um pode escrever isso).</summary>
    FileVersion,
    Unknown,
}

public sealed record FilePublisher(string? Name, PublisherSource Source)
{
    public static readonly FilePublisher Unknown = new(null, PublisherSource.Unknown);

    public string Describe() => Source switch
    {
        PublisherSource.Signature => Name ?? "assinado",
        PublisherSource.SignatureNotTrusted => $"{Name ?? "assinatura"} (assinatura não validada)",
        PublisherSource.FileVersion => $"{Name} (não assinado; dado do arquivo)",
        _ => "desconhecido",
    };
}

/// <summary>Leitura de arquivos (existência, listagem rasa, editor). Nunca segue links simbólicos/junções ao listar.</summary>
public interface IFileInspector
{
    bool Exists(string path);
    IReadOnlyList<string> Files(string folder);
    FilePublisher Publisher(string path);
    /// <summary>Bytes do início de um arquivo pequeno (atalhos .lnk), ou null.</summary>
    byte[]? ReadSmall(string path, int maxBytes);
}

public enum TaskTriggerKind { Boot, Logon, Other }

public sealed record ScheduledTaskInfo(
    string Path,
    bool Enabled,
    IReadOnlyList<TaskTriggerKind> Triggers,
    string? Command,
    string? Arguments,
    string? Author,
    string? UserId,
    bool RunsElevated)
{
    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    public bool IsStartupTask => Triggers.Any(t => t is TaskTriggerKind.Boot or TaskTriggerKind.Logon);
    public bool IsMicrosoftTask => BootTaskRules.IsMicrosoftPath(Path);
}

public interface IScheduledTaskReader
{
    /// <summary>Tarefas visíveis para quem lê (todas as pastas, inclusive ocultas).</summary>
    IReadOnlyList<ScheduledTaskInfo> All();
}

/// <summary>Configuração e estado de um serviço (leitura).</summary>
public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string? Description,
    string StartType,
    string State,
    string? Account,
    IReadOnlyList<string> Dependencies,
    string? ImagePath,
    string? ServiceDll,
    int ServiceType)
{
    public bool IsPerUser => ServicePolicy.IsPerUserService(ServiceType);
    public bool IsWin32 => ServicePolicy.IsWin32Service(ServiceType);
    public bool IsAutomatic => StartType is "Automatic" or "AutomaticDelayed";
    public bool IsRunning => State == "Running";
}

public interface IServiceReader
{
    IReadOnlyList<ServiceInfo> All();
}

/// <summary>Pastas Inicializar (do usuário e comum). Null quando não foi possível determinar.</summary>
public sealed record StartupFolders(string? User, string? Common);
