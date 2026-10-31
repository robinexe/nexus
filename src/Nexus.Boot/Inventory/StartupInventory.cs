using Nexus.Boot.Impact;
using Nexus.WindowsActions.Registry;

namespace Nexus.Boot.Inventory;

public enum StartupSource
{
    RunUser,
    RunMachine,
    RunMachine32,
    RunOnceUser,
    RunOnceMachine,
    RunOnceMachine32,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask,
    Service,
}

/// <summary>Um item que abre com o Windows. Nada aqui é inferido sem dizer: <see cref="FileExists"/> null = não verificável.</summary>
public sealed record StartupItem
{
    public required string Name { get; init; }
    public required StartupSource Source { get; init; }
    /// <summary>Identificador na origem: nome do valor (Run), nome do arquivo (pasta), caminho da tarefa, nome do serviço.</summary>
    public required string EntryName { get; init; }
    public string? Command { get; init; }
    public string? ExecutablePath { get; init; }
    public bool? FileExists { get; init; }
    public bool Enabled { get; init; } = true;
    public FilePublisher Publisher { get; init; } = FilePublisher.Unknown;
    public ImpactAssessment Impact { get; init; } = new(ImpactLevel.Unknown, "Não avaliado.");
    /// <summary>Tarefa da Microsoft, executada com privilégios elevados, ou de outro usuário (só para tarefas).</summary>
    public bool TaskNeedsAdmin { get; init; }
    public bool IsMicrosoft { get; init; }

    /// <summary>Entrada de registro/pasta cujo executável comprovadamente não existe mais.</summary>
    public bool IsOrphan => FileExists == false;

    public string SourceLabel => Source switch
    {
        StartupSource.RunUser => "Registro do usuário (Run)",
        StartupSource.RunMachine => "Registro do computador (Run)",
        StartupSource.RunMachine32 => "Registro do computador, 32 bits (Run)",
        StartupSource.RunOnceUser => "Registro do usuário (RunOnce, uma vez)",
        StartupSource.RunOnceMachine => "Registro do computador (RunOnce, uma vez)",
        StartupSource.RunOnceMachine32 => "Registro do computador, 32 bits (RunOnce, uma vez)",
        StartupSource.StartupFolderUser => "Pasta Inicializar do usuário",
        StartupSource.StartupFolderCommon => "Pasta Inicializar de todos os usuários",
        StartupSource.ScheduledTask => "Tarefa agendada (logon/boot)",
        StartupSource.Service => "Serviço automático",
        _ => Source.ToString(),
    };

    /// <summary>Precisa de administrador para mudar.</summary>
    public bool NeedsAdmin => Source switch
    {
        StartupSource.RunUser or StartupSource.RunOnceUser or StartupSource.StartupFolderUser => false,
        StartupSource.ScheduledTask => TaskNeedsAdmin,
        _ => true,
    };

    /// <summary>Pode ser ligado/desligado pelo mesmo método do Gerenciador de Tarefas (StartupApproved) ou da tarefa.</summary>
    public bool CanToggle => Source is StartupSource.RunUser or StartupSource.RunMachine or StartupSource.RunMachine32
        or StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon
        || (Source == StartupSource.ScheduledTask && !IsMicrosoft);

    /// <summary>Entrada órfã removível (com backup): só valores Run.</summary>
    public bool CanRemoveOrphan => IsOrphan && Source is StartupSource.RunUser or StartupSource.RunMachine or StartupSource.RunMachine32;
}

public sealed record StartupInventoryResult(IReadOnlyList<StartupItem> Items, IReadOnlyList<string> Notes);

/// <summary>Monta o inventário de inicialização a partir de leitores (reais no Windows, falsos nos testes). Só lê.</summary>
public sealed class StartupInventory
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly IBootRegistryReader _reg;
    private readonly IFileInspector _files;
    private readonly Func<string, string?> _env;
    private readonly string _windowsDirectory;

    /// <param name="environment">Variáveis de ambiente de quem usa (expansão de %VAR%).</param>
    public StartupInventory(IBootRegistryReader registry, IFileInspector files, Func<string, string?> environment, string windowsDirectory)
    {
        _reg = registry;
        _files = files;
        _env = environment;
        _windowsDirectory = windowsDirectory;
    }

    public StartupInventoryResult Read(StartupFolders folders, IScheduledTaskReader? tasks, IServiceReader? services, BootReport? report)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var notes = new List<string>();
        var items = new List<StartupItem>();
        report ??= BootReport.Unreadable("eventos não lidos");

        items.AddRange(RunEntries(RegHive.HKCU, RunPath, RegView.Default, StartupSource.RunUser, "Run", report));
        items.AddRange(RunEntries(RegHive.HKLM, RunPath, RegView.Registry64, StartupSource.RunMachine, "Run", report));
        items.AddRange(RunEntries(RegHive.HKLM, RunPath, RegView.Registry32, StartupSource.RunMachine32, "Run32", report));
        items.AddRange(RunEntries(RegHive.HKCU, RunOncePath, RegView.Default, StartupSource.RunOnceUser, null, report));
        items.AddRange(RunEntries(RegHive.HKLM, RunOncePath, RegView.Registry64, StartupSource.RunOnceMachine, null, report));
        items.AddRange(RunEntries(RegHive.HKLM, RunOncePath, RegView.Registry32, StartupSource.RunOnceMachine32, null, report));

        if (folders.User is null) notes.Add("Pasta Inicializar do usuário não encontrada.");
        else items.AddRange(FolderEntries(folders.User, RegHive.HKCU, StartupSource.StartupFolderUser, report));
        if (folders.Common is null) notes.Add("Pasta Inicializar comum não encontrada.");
        else items.AddRange(FolderEntries(folders.Common, RegHive.HKLM, StartupSource.StartupFolderCommon, report));

        if (tasks is null) notes.Add("Tarefas agendadas não lidas.");
        else
        {
            try { items.AddRange(TaskEntries(tasks.All(), report)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add($"Tarefas agendadas não lidas: {ex.Message}"); }
        }

        if (services is null) notes.Add("Serviços não lidos.");
        else
        {
            try { items.AddRange(ServiceEntries(services.All(), report)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add($"Serviços não lidos: {ex.Message}"); }
        }

        if (report.Unavailable is not null) notes.Add("Impacto no boot desconhecido: " + report.Unavailable);
        return new StartupInventoryResult(items, notes);
    }

    /// <summary>Estado em StartupApproved: ausente = habilitado (como o Explorer); byte 0 ímpar = desabilitado.</summary>
    public static bool IsApprovedEnabled(string? hexData)
    {
        if (string.IsNullOrEmpty(hexData) || hexData.Length < 2) return true;
        return !byte.TryParse(hexData.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var b) || (b & 0x1) == 0;
    }

    private Dictionary<string, string> Approved(RegHive hive, string kind) =>
        _reg.Values(hive, $@"{ApprovedPath}\{kind}", RegView.Default)
            .Where(v => v.Kind.Equals("Binary", StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Data, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<StartupItem> RunEntries(RegHive hive, string path, RegView view, StartupSource source, string? approvedKind, BootReport report)
    {
        var approved = approvedKind is null ? new Dictionary<string, string>() : Approved(hive, approvedKind);
        foreach (var v in _reg.Values(hive, path, view))
        {
            if (v.Name.Length == 0 || !(v.Kind is "String" or "ExpandString")) continue;
            var (exe, exists) = Resolve(v.Data);
            var publisher = exists == true ? SafePublisher(exe!) : FilePublisher.Unknown;
            yield return new StartupItem
            {
                Name = v.Name,
                Source = source,
                EntryName = v.Name,
                Command = v.Data,
                ExecutablePath = exe,
                FileExists = exists,
                Enabled = !approved.TryGetValue(v.Name, out var raw) || IsApprovedEnabled(raw),
                Publisher = publisher,
                Impact = report.ImpactOf(exe is null ? null : CommandLine.FileNameOf(exe)),
                IsMicrosoft = IsMicrosoftPublisher(publisher),
            };
        }
    }

    private IEnumerable<StartupItem> FolderEntries(string folder, RegHive hive, StartupSource source, BootReport report)
    {
        var approved = Approved(hive, "StartupFolder");
        foreach (var file in _files.Files(folder))
        {
            var name = CommandLine.FileNameOf(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string? target = file;
            bool? exists = true;
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = _files.ReadSmall(file, 64 * 1024);
                var t = bytes is null ? null : ShellLink.TargetPath(bytes);
                if (t is null) { target = null; exists = null; }
                else
                {
                    var expanded = CommandLine.Expand(t, _env);
                    target = expanded ?? t;
                    exists = expanded is not null && CommandLine.IsAbsoluteWindowsPath(expanded) ? _files.Exists(expanded) : null;
                }
            }
            var publisher = exists == true && target is not null ? SafePublisher(target) : FilePublisher.Unknown;
            yield return new StartupItem
            {
                Name = Path.GetFileNameWithoutExtension(name),
                Source = source,
                EntryName = name,
                Command = target,
                ExecutablePath = target,
                FileExists = exists,
                Enabled = !approved.TryGetValue(name, out var raw) || IsApprovedEnabled(raw),
                Publisher = publisher,
                Impact = report.ImpactOf(target is null ? null : CommandLine.FileNameOf(target)),
                IsMicrosoft = IsMicrosoftPublisher(publisher),
            };
        }
    }

    private IEnumerable<StartupItem> TaskEntries(IReadOnlyList<ScheduledTaskInfo> tasks, BootReport report)
    {
        foreach (var t in tasks.Where(t => t.IsStartupTask))
        {
            var exe = t.Command is null ? null : CommandLine.Expand(t.Command.Trim('"'), _env);
            bool? exists = exe is not null && CommandLine.IsAbsoluteWindowsPath(exe) ? _files.Exists(exe) : null;
            var publisher = exists == true ? SafePublisher(exe!) : FilePublisher.Unknown;
            yield return new StartupItem
            {
                Name = t.Name,
                Source = StartupSource.ScheduledTask,
                EntryName = t.Path,
                Command = t.Command is null ? "(ação que não é um programa)" : $"{t.Command} {t.Arguments}".Trim(),
                ExecutablePath = exe,
                FileExists = exists,
                Enabled = t.Enabled,
                Publisher = publisher,
                Impact = report.ImpactOf(exe is null ? null : CommandLine.FileNameOf(exe)),
                TaskNeedsAdmin = t.RunsElevated || !BootTaskRules.IsCurrentUserPrincipal(t.UserId, _env("USERNAME")),
                IsMicrosoft = t.IsMicrosoftTask,
            };
        }
    }

    private IEnumerable<StartupItem> ServiceEntries(IReadOnlyList<ServiceInfo> services, BootReport report)
    {
        foreach (var s in services.Where(s => s.IsAutomatic && s.IsWin32 && !s.IsPerUser))
        {
            var binary = ServiceBinary(s);
            bool? exists = binary is not null ? _files.Exists(binary) : null;
            var publisher = exists == true ? SafePublisher(binary!) : FilePublisher.Unknown;
            if (IsMicrosoftPublisher(publisher)) continue; // só terceiros: os do Windows ficam na página Serviços
            yield return new StartupItem
            {
                Name = s.DisplayName,
                Source = StartupSource.Service,
                EntryName = s.Name,
                Command = s.ImagePath,
                ExecutablePath = binary,
                FileExists = exists,
                Enabled = true,
                Publisher = publisher,
                Impact = report.ImpactOf(binary is null ? null : CommandLine.FileNameOf(binary), s.Name),
            };
        }
    }

    /// <summary>Arquivo que identifica o serviço: a DLL (svchost) ou o executável.</summary>
    public string? ServiceBinary(ServiceInfo s)
    {
        var raw = s.ServiceDll ?? CommandLine.ExecutableOf(s.ImagePath);
        if (raw is null) return null;
        var expanded = CommandLine.Expand(CommandLine.NormalizeServicePath(raw, _windowsDirectory), _env);
        return expanded is not null && CommandLine.IsAbsoluteWindowsPath(expanded) ? expanded : null;
    }

    public static bool IsMicrosoftPublisher(FilePublisher? p) =>
        p is { Name: { } n, Source: PublisherSource.Signature or PublisherSource.FileVersion } &&
        n.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    private FilePublisher SafePublisher(string path)
    {
        try { return _files.Publisher(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return FilePublisher.Unknown; }
    }

    /// <summary>Executável e se existe (null = não dá para verificar: sem caminho absoluto ou variável desconhecida).</summary>
    private (string? Exe, bool? Exists) Resolve(string command)
    {
        var exe = CommandLine.ExecutableOf(command);
        if (exe is null) return (null, null);
        var expanded = CommandLine.Expand(exe, _env);
        if (expanded is null) return (exe, null);
        if (!CommandLine.IsAbsoluteWindowsPath(expanded))
        {
            // Nome solto (ex.: "ctfmon.exe"): procura só onde o Windows procuraria primeiro; não achar não prova que não existe.
            foreach (var dir in new[] { Path.Combine(_windowsDirectory, "System32"), _windowsDirectory })
            {
                var candidate = dir.TrimEnd('\\') + "\\" + expanded;
                if (_files.Exists(candidate)) return (candidate, true);
            }
            return (expanded, null);
        }
        return (expanded, _files.Exists(expanded));
    }
}

/// <summary>Regras de tarefas agendadas (puras).</summary>
public static class BootTaskRules
{
    /// <summary>Tarefas da Microsoft (\Microsoft\...): parte do Windows; o NEXUS só mostra, não desliga.</summary>
    public static bool IsMicrosoftPath(string path) =>
        path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A tarefa roda como o próprio usuário (DOMINIO\nome, nome ou SID não-sistema com o mesmo nome).</summary>
    public static bool IsCurrentUserPrincipal(string? userId, string? userName)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userName)) return false;
        var tail = userId[(userId.LastIndexOf('\\') + 1)..];
        return string.Equals(tail, userName, StringComparison.OrdinalIgnoreCase);
    }
}
