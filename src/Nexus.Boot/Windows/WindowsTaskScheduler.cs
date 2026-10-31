using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Nexus.Boot.Handlers;
using Nexus.Boot.Inventory;

namespace Nexus.Boot.Windows;

/// <summary>
/// Agendador de Tarefas pela API COM oficial ("Schedule.Service", Unicode correto). Se a COM falhar, a leitura cai para
/// <c>schtasks /query /xml</c> (mesmo esquema XML, lido por <see cref="TaskXml"/>) e a escrita para <c>schtasks /change</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskScheduler : IScheduledTaskReader, IScheduledTaskBackend
{
    private const int TaskEnumHidden = 1, TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0, RunLevelHighest = 1;

    public IReadOnlyList<ScheduledTaskInfo> All()
    {
        try { return ComAll(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or UnauthorizedAccessException or NotSupportedException or TypeLoadException)
        {
            return TaskXml.ParseMany(Schtasks(["/query", "/xml"]) ?? throw new InvalidOperationException("Agendador de Tarefas indisponível."));
        }
    }

    public ScheduledTaskInfo? Find(string taskPath)
    {
        try
        {
            var task = ComTask(taskPath);
            return task is null ? null : Describe(task);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NotSupportedException or TypeLoadException)
        {
            var xml = Schtasks(["/query", "/xml", "/tn", taskPath]);
            return xml is null ? null : (TaskXml.ParseMany($"<!-- {taskPath} -->\n" + xml) is { Count: > 0 } found ? found[0] : null)
                                        ?? TaskXml.Parse(taskPath, xml[Math.Max(0, xml.IndexOf("<Task", StringComparison.Ordinal))..]);
        }
    }

    public void SetEnabled(string taskPath, bool enabled)
    {
        dynamic? task;
        try { task = ComTask(taskPath); }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NotSupportedException or TypeLoadException)
        {
            task = null;
            if (Schtasks(["/change", "/tn", taskPath, enabled ? "/enable" : "/disable"]) is null)
                throw new InvalidOperationException("schtasks recusou a mudança (permissão?).");
            return;
        }
        if (task is null) throw new InvalidOperationException("Tarefa não encontrada.");
        try { task.Enabled = enabled; }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Sem permissão para alterar esta tarefa: ela exige administrador."); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005)) { throw new InvalidOperationException("Sem permissão para alterar esta tarefa: ela exige administrador."); }
    }

    // ------------------------------------------------------------------ COM

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic? ComTask(string taskPath)
    {
        var service = Connect();
        var cut = taskPath.LastIndexOf('\\');
        var folderPath = cut <= 0 ? "\\" : taskPath[..cut];
        dynamic folder;
        try { folder = service.GetFolder(folderPath); }
        catch (COMException) { return null; }
        try { return folder.GetTask(taskPath[(cut + 1)..]); }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    private static List<ScheduledTaskInfo> ComAll()
    {
        var list = new List<ScheduledTaskInfo>();
        var service = Connect();
        Walk(service.GetFolder("\\"), list, 0);
        return list;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> list, int depth)
    {
        if (depth > 10) return;
        dynamic tasks = folder.GetTasks(TaskEnumHidden);
        int count = tasks.Count;
        for (var i = 1; i <= count; i++)
        {
            try { list.Add(Describe(tasks[i])); }
            catch (COMException) { } // tarefa ilegível para esta conta: fica de fora
        }
        dynamic folders = folder.GetFolders(0);
        int fc = folders.Count;
        for (var i = 1; i <= fc; i++)
        {
            try { Walk(folders[i], list, depth + 1); }
            catch (COMException) { } // pasta sem permissão de leitura
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ScheduledTaskInfo Describe(dynamic task)
    {
        string path = task.Path;
        bool enabled = task.Enabled;
        dynamic def = task.Definition;
        var triggers = new List<TaskTriggerKind>();
        dynamic trig = def.Triggers;
        int tc = trig.Count;
        for (var i = 1; i <= tc; i++)
        {
            dynamic t = trig[i];
            bool tEnabled = t.Enabled;
            if (!tEnabled) continue;
            int type = t.Type;
            triggers.Add(type == TriggerBoot ? TaskTriggerKind.Boot : type == TriggerLogon ? TaskTriggerKind.Logon : TaskTriggerKind.Other);
        }
        string? command = null, arguments = null;
        dynamic actions = def.Actions;
        int ac = actions.Count;
        for (var i = 1; i <= ac && command is null; i++)
        {
            dynamic a = actions[i];
            int at = a.Type;
            if (at != ActionExec) continue;
            command = a.Path as string;
            arguments = a.Arguments as string;
        }
        string? author = null, user = null;
        var elevated = false;
        try { author = def.RegistrationInfo.Author as string; } catch (COMException) { }
        try
        {
            dynamic principal = def.Principal;
            user = principal.UserId as string;
            int level = principal.RunLevel;
            elevated = level == RunLevelHighest;
        }
        catch (COMException) { }
        return new ScheduledTaskInfo(path, enabled, triggers, command?.Trim(), arguments?.Trim(), author, user, elevated);
    }

    // ------------------------------------------------------------------ schtasks (alternativa)

    /// <summary>Executa schtasks.exe do System32 (caminho fixo, argumentos em lista, sem shell). Null se falhar.</summary>
    private static string? Schtasks(IReadOnlyList<string> args)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
        return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
    }
}
