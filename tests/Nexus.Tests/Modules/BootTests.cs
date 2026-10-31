using System.Text;
using Nexus.Boot;
using Nexus.Boot.Handlers;
using Nexus.Boot.Impact;
using Nexus.Boot.Inventory;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;

namespace Nexus.Tests.Modules;

[Trait("Category", "Unit")]
public sealed class BootTests
{
    // ------------------------------------------------------------------ apoio

    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static string Ev(int id, string time, params (string Name, string Value)[] data)
    {
        var sb = new StringBuilder();
        sb.Append("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>")
          .Append("<Provider Name='Microsoft-Windows-Diagnostics-Performance' Guid='{CFC18EC0-96B1-4EBA-961B-622CAEE05B0A}'/>")
          .Append("<EventID>").Append(id).Append("</EventID><Version>2</Version><Level>2</Level>")
          .Append("<TimeCreated SystemTime='").Append(time).Append("'/>")
          .Append("<Channel>Microsoft-Windows-Diagnostics-Performance/Operational</Channel></System><EventData>");
        foreach (var (n, v) in data) sb.Append("<Data Name='").Append(n).Append("'>").Append(v).Append("</Data>");
        return sb.Append("</EventData></Event>").ToString();
    }

    private static string BootEvent(string time, long totalMs, long mainMs, long postMs) =>
        Ev(100, time, ("BootStartTime", time), ("BootTime", totalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("MainPathBootTime", mainMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("BootPostBootTime", postMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("BootIsDegradation", "false"), ("BootNumStartupApps", "14"));

    private static string AppEvent(string time, string file, string friendly, long totalMs, long degradationMs) =>
        Ev(101, time, ("Name", file), ("FriendlyName", friendly), ("Version", "1.0"),
            ("TotalTime", totalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("DegradationTime", degradationMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private sealed class FakeRegistry : IBootRegistryReader
    {
        private readonly Dictionary<string, List<RegValue>> _keys = new(StringComparer.OrdinalIgnoreCase);
        private static string K(RegHive h, string p, RegView v) => $"{h}|{v}|{p}";
        public FakeRegistry Add(RegHive h, string p, RegView v, string name, string kind, string data)
        {
            if (!_keys.TryGetValue(K(h, p, v), out var list)) _keys[K(h, p, v)] = list = [];
            list.Add(new RegValue(name, kind, data));
            return this;
        }
        public IReadOnlyList<RegValue> Values(RegHive hive, string path, RegView view) => _keys.TryGetValue(K(hive, path, view), out var l) ? l : [];
    }

    private sealed class FakeFiles : IFileInspector
    {
        public readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, FilePublisher> Publishers = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, byte[]> Bytes = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Existing.Contains(path);
        public IReadOnlyList<string> Files(string folder) => Folders.TryGetValue(folder, out var f) ? f : [];
        public FilePublisher Publisher(string path) => Publishers.TryGetValue(path, out var p) ? p : FilePublisher.Unknown;
        public byte[]? ReadSmall(string path, int maxBytes) => Bytes.TryGetValue(path, out var b) ? b : null;
    }

    private sealed class FakeTasks : IScheduledTaskReader, IScheduledTaskBackend
    {
        public readonly Dictionary<string, ScheduledTaskInfo> Tasks = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ScheduledTaskInfo> All() => Tasks.Values.ToList();
        public ScheduledTaskInfo? Find(string taskPath) => Tasks.TryGetValue(taskPath, out var t) ? t : null;
        public void SetEnabled(string taskPath, bool enabled) => Tasks[taskPath] = Tasks[taskPath] with { Enabled = enabled };
    }

    private sealed class FakeServiceReader(params ServiceInfo[] services) : IServiceReader
    {
        public IReadOnlyList<ServiceInfo> All() => services;
    }

    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, string> Start = new(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        public string? GetStartType(string service) => Start.TryGetValue(service, out var v) ? v : null;
        public void SetStartType(string service, string startType) { Writes++; Start[service] = startType; }
    }

    private static string Env(string name) => name.ToUpperInvariant() switch
    {
        "SYSTEMROOT" or "WINDIR" => @"C:\Windows",
        "PROGRAMFILES" => @"C:\Program Files",
        "LOCALAPPDATA" => @"C:\Users\ana\AppData\Local",
        "APPDATA" => @"C:\Users\ana\AppData\Roaming",
        "USERNAME" => "ana",
        _ => null!,
    };

    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    /// <summary>Monta a ação como o broker: valor escolhido, argumentos validados pelo catálogo, depois TryInstantiate.</summary>
    private static ActionInstance FromCatalog(string key, string? value, Dictionary<string, string>? args = null)
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == key);
        if (entry.DesiredValue is null) entry = entry with { DesiredValue = value };
        var validated = entry.ValidateUserArguments(args, out var reason);
        Assert.True(validated is not null, reason);
        var inst = entry.TryInstantiate(new Dictionary<string, string>(validated!), out reason);
        Assert.True(inst is not null, reason);
        return inst!;
    }

    private static (InMemoryJournal Journal, ActionExecutor Exec, DesiredStateResolver Resolver) Engine(params IActionHandler[] handlers)
    {
        var registry = new HandlerRegistry();
        foreach (var h in handlers) registry.Add(h);
        var journal = new InMemoryJournal();
        return (journal, new ActionExecutor(registry, journal), new DesiredStateResolver(registry));
    }

    // ------------------------------------------------------------------ eventos do boot

    [Fact]
    public void Parses_boot_and_slow_app_events_from_xml()
    {
        var boot = BootEvents.Parse(BootEvent("2026-10-30T11:02:03.1234567Z", 38_200, 24_100, 14_100));
        Assert.NotNull(boot);
        Assert.Equal(100, boot!.EventId);
        var record = BootEvents.AsBoot(boot)!;
        Assert.Equal(38_200, record.TotalMs);
        Assert.Equal(24_100, record.MainPathMs);
        Assert.Equal(14_100, record.PostBootMs);
        Assert.Equal(14, record.StartupApps);
        Assert.False(record.IsDegradation);

        var slow = BootEvents.AsSlow(BootEvents.Parse(AppEvent("2026-10-30T11:03:00Z", "OneDrive.exe", "Microsoft OneDrive", 6200, 4100))!)!;
        Assert.Equal(SlowKind.Application, slow.Kind);
        Assert.Equal("OneDrive.exe", slow.Name);
        Assert.Equal(6200, slow.TotalMs);
        Assert.Equal(4100, slow.DegradationMs);
        Assert.Equal("Microsoft OneDrive (OneDrive.exe)", slow.Label);

        var svc = BootEvents.AsSlow(BootEvents.Parse(Ev(103, "2026-10-30T11:03:00Z", ("Name", "AppleMobileDeviceService.exe"), ("TotalTime", "900")))!)!;
        Assert.Equal(SlowKind.Service, svc.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Event><System><EventID>abc</EventID></System></Event>")]
    [InlineData("não é xml")]
    [InlineData("<!DOCTYPE x [<!ENTITY a 'b'>]><Event/>")]
    public void Malformed_events_are_ignored_not_thrown(string xml) => Assert.Null(BootEvents.Parse(xml));

    [Fact]
    public void Boot_with_zero_duration_is_not_a_boot()
    {
        var e = BootEvents.Parse(BootEvent("2026-10-30T11:02:03Z", 0, 0, 0))!;
        Assert.Null(BootEvents.AsBoot(e));
    }

    [Fact]
    public void Report_finds_last_boot_and_its_main_delay()
    {
        var report = BootReport.FromXml(
        [
            BootEvent("2026-10-28T08:00:00Z", 52_000, 30_000, 22_000),
            AppEvent("2026-10-28T08:01:00Z", "Discord.exe", "Discord", 8000, 5000),
            BootEvent("2026-10-30T11:00:00Z", 38_000, 24_000, 14_000),
            AppEvent("2026-10-30T11:01:00Z", "OneDrive.exe", "Microsoft OneDrive", 6200, 4100),
            AppEvent("2026-10-30T11:01:30Z", "Steam.exe", "Steam", 2100, 900),
        ]);
        Assert.Equal(2, report.Boots.Count);
        Assert.Equal(38_000, report.Last!.TotalMs);
        Assert.Equal("OneDrive.exe", report.MainDelayOfLastBoot!.Name);
        Assert.Equal(2, report.SlowFor(report.Last).Count); // Discord pertence ao boot anterior
    }

    [Fact]
    public void Impact_comes_only_from_real_measurements()
    {
        var report = BootReport.FromXml(
        [
            BootEvent("2026-10-30T11:00:00Z", 38_000, 24_000, 14_000),
            AppEvent("2026-10-30T11:01:00Z", "OneDrive.exe", "OneDrive", 6200, 4100),
            AppEvent("2026-10-30T11:01:10Z", "Steam.exe", "Steam", 2100, 900),
            AppEvent("2026-10-30T11:01:20Z", "rgb.exe", "RGB", 400, 100),
            Ev(103, "2026-10-30T11:01:30Z", ("Name", "AppleMobileDeviceService.exe"), ("TotalTime", "3500")),
        ]);
        Assert.Equal(ImpactLevel.High, report.ImpactOf("onedrive.exe").Level);
        Assert.Equal(ImpactLevel.Medium, report.ImpactOf("Steam.exe").Level);
        Assert.Equal(ImpactLevel.Low, report.ImpactOf("rgb.exe").Level);
        Assert.Equal(ImpactLevel.High, report.ImpactOf("AppleMobileDeviceService.exe", "Apple Mobile Device Service").Level);
        // Sem registro: desconhecido, nunca "baixo".
        var none = report.ImpactOf("Spotify.exe");
        Assert.Equal(ImpactLevel.Unknown, none.Level);
        Assert.Equal("Desconhecido", none.Label);
        Assert.Contains("não registrou", none.Reason, StringComparison.Ordinal);
        // Sem permissão para ler o log: desconhecido com o motivo.
        var denied = BootReport.Unreadable("exige administrador");
        Assert.Equal(ImpactLevel.Unknown, denied.ImpactOf("OneDrive.exe").Level);
        Assert.Contains("administrador", denied.ImpactOf("OneDrive.exe").Reason, StringComparison.Ordinal);
        Assert.Null(denied.Last);
    }

    // ------------------------------------------------------------------ linha de comando e atalhos

    [Theory]
    [InlineData("\"C:\\Program Files\\Steam\\steam.exe\" -silent", "C:\\Program Files\\Steam\\steam.exe")]
    [InlineData("C:\\Program Files\\Tool\\tool.exe /tray", "C:\\Program Files\\Tool\\tool.exe")]
    [InlineData("%LOCALAPPDATA%\\Discord\\Update.exe --processStart Discord.exe", "%LOCALAPPDATA%\\Discord\\Update.exe")]
    [InlineData("rundll32.exe C:\\x\\y.dll,Entry", "rundll32.exe")]
    [InlineData("C:\\a.company\\run.bat x", "C:\\a.company\\run.bat")]
    [InlineData("ctfmon", "ctfmon")]
    [InlineData("   ", null)]
    public void Extracts_executable_from_command(string command, string? expected) => Assert.Equal(expected, CommandLine.ExecutableOf(command));

    [Fact]
    public void Expansion_refuses_unknown_variables()
    {
        Assert.Equal(@"C:\Program Files\x.exe", CommandLine.Expand(@"%ProgramFiles%\x.exe", Env));
        Assert.Null(CommandLine.Expand(@"%NAOEXISTE%\x.exe", Env));
        Assert.Equal(@"C:\Windows\System32\drivers\x.sys", CommandLine.NormalizeServicePath(@"\SystemRoot\System32\drivers\x.sys", @"C:\Windows"));
        Assert.Equal(@"C:\x\svc.exe", CommandLine.NormalizeServicePath(@"\??\C:\x\svc.exe", @"C:\Windows"));
    }

    private static byte[] Lnk(string localPath)
    {
        var path = Encoding.Latin1.GetBytes(localPath + "\0");
        var info = new byte[0x1C + path.Length + 1];
        BitConverter.GetBytes(info.Length).CopyTo(info, 0);
        BitConverter.GetBytes(0x1C).CopyTo(info, 4);      // LinkInfoHeaderSize
        BitConverter.GetBytes(1).CopyTo(info, 8);         // VolumeIDAndLocalBasePath
        BitConverter.GetBytes(0x1C).CopyTo(info, 12);     // VolumeIDOffset (não usado pelo leitor)
        BitConverter.GetBytes(0x1C).CopyTo(info, 16);     // LocalBasePathOffset
        BitConverter.GetBytes(info.Length - 1).CopyTo(info, 24); // CommonPathSuffixOffset ("")
        path.CopyTo(info, 0x1C);
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        BitConverter.GetBytes(0x2).CopyTo(header, 0x14);  // HasLinkInfo
        return [.. header, .. info];
    }

    [Fact]
    public void Reads_shortcut_target_and_survives_garbage()
    {
        Assert.Equal(@"C:\Tools\Macro\macro.exe", ShellLink.TargetPath(Lnk(@"C:\Tools\Macro\macro.exe")));
        Assert.Null(ShellLink.TargetPath(new byte[10]));
        var broken = Lnk(@"C:\x.exe");
        BitConverter.GetBytes(99999).CopyTo(broken, 0x4C); // tamanho do LinkInfo absurdo
        Assert.Null(ShellLink.TargetPath(broken));
    }

    // ------------------------------------------------------------------ inventário

    [Fact]
    public void Inventory_reads_every_source_with_state_publisher_and_orphans()
    {
        var reg = new FakeRegistry()
            .Add(RegHive.HKCU, StartupInventory.RunPath, RegView.Default, "Discord", "String", @"%LOCALAPPDATA%\Discord\Update.exe --processStart Discord.exe")
            .Add(RegHive.HKCU, StartupInventory.RunPath, RegView.Default, "Velho", "String", @"""C:\Program Files\Velho\velho.exe"" /min")
            .Add(RegHive.HKCU, StartupInventory.ApprovedPath + @"\Run", RegView.Default, "Discord", "Binary", "030000000000000000000000")
            .Add(RegHive.HKLM, StartupInventory.RunPath, RegView.Registry64, "SecurityHealth", "ExpandString", @"%windir%\system32\SecurityHealthSystray.exe")
            .Add(RegHive.HKLM, StartupInventory.RunPath, RegView.Registry32, "Steam", "String", @"""C:\Program Files\Steam\steam.exe"" -silent")
            .Add(RegHive.HKLM, StartupInventory.ApprovedPath + @"\Run32", RegView.Default, "Steam", "Binary", "020000000000000000000000")
            .Add(RegHive.HKCU, StartupInventory.RunOncePath, RegView.Default, "Setup", "String", @"C:\Temp\setup.exe");
        var files = new FakeFiles();
        files.Existing.UnionWith([@"C:\Users\ana\AppData\Local\Discord\Update.exe", @"C:\Windows\system32\SecurityHealthSystray.exe",
            @"C:\Program Files\Steam\steam.exe", @"C:\Tools\Macro\macro.exe", @"C:\Program Files\Logi\lghub.exe", @"C:\Program Files\Apple\amds.exe",
            @"C:\Windows\System32\svchost.exe"]);
        files.Publishers[@"C:\Program Files\Steam\steam.exe"] = new FilePublisher("Valve Corp.", PublisherSource.Signature);
        files.Publishers[@"C:\Windows\system32\SecurityHealthSystray.exe"] = new FilePublisher("Microsoft Windows", PublisherSource.Signature);
        files.Publishers[@"C:\Program Files\Apple\amds.exe"] = new FilePublisher("Apple Inc.", PublisherSource.Signature);
        files.Publishers[@"C:\Windows\System32\svchost.exe"] = new FilePublisher("Microsoft Windows", PublisherSource.Signature);
        files.Folders[@"C:\Users\ana\Startup"] = [@"C:\Users\ana\Startup\Macro.lnk", @"C:\Users\ana\Startup\desktop.ini"];
        files.Bytes[@"C:\Users\ana\Startup\Macro.lnk"] = Lnk(@"C:\Tools\Macro\macro.exe");
        var tasks = new FakeTasks();
        tasks.Tasks[@"\LGHUB Updater"] = new ScheduledTaskInfo(@"\LGHUB Updater", true, [TaskTriggerKind.Logon], @"C:\Program Files\Logi\lghub.exe", "--updater", "Logitech", @"PC\ana", false);
        tasks.Tasks[@"\Backup diário"] = new ScheduledTaskInfo(@"\Backup diário", true, [TaskTriggerKind.Other], @"C:\b.exe", null, null, @"PC\ana", false);
        tasks.Tasks[@"\Microsoft\Windows\X\Y"] = new ScheduledTaskInfo(@"\Microsoft\Windows\X\Y", true, [TaskTriggerKind.Boot], @"%windir%\system32\y.exe", null, "Microsoft", "SYSTEM", true);
        var services = new FakeServiceReader(
            new ServiceInfo("Apple Mobile Device Service", "Apple Mobile Device Service", null, "Automatic", "Running", "LocalSystem", [], @"""C:\Program Files\Apple\amds.exe""", null, 0x10),
            new ServiceInfo("Dnscache", "Cliente DNS", null, "Automatic", "Running", "NT AUTHORITY\\NetworkService", [], @"C:\Windows\System32\svchost.exe -k NetworkService", null, 0x20),
            new ServiceInfo("CDPUserSvc_1a2b", "Plataforma de Dispositivos Conectados", null, "Automatic", "Running", null, [], @"C:\Windows\System32\svchost.exe", null, 0xE0),
            new ServiceInfo("Fax", "Fax", null, "Manual", "Stopped", null, [], @"C:\Windows\System32\fxssvc.exe", null, 0x10));
        var report = BootReport.FromXml([BootEvent("2026-10-30T11:00:00Z", 38_000, 24_000, 14_000), AppEvent("2026-10-30T11:01:00Z", "steam.exe", "Steam", 4000, 2000)]);

        var inv = new StartupInventory(reg, files, Env, @"C:\Windows").Read(new StartupFolders(@"C:\Users\ana\Startup", null), tasks, services, report);
        var byName = inv.Items.ToDictionary(i => i.Name, StringComparer.Ordinal);

        Assert.False(byName["Discord"].Enabled);                      // desabilitado em StartupApproved
        Assert.True(byName["Discord"].FileExists);
        Assert.Equal(StartupSource.RunUser, byName["Discord"].Source);
        Assert.False(byName["Discord"].NeedsAdmin);

        Assert.True(byName["Velho"].IsOrphan);                        // programa apagado
        Assert.True(byName["Velho"].CanRemoveOrphan);

        Assert.True(byName["Steam"].Enabled);
        Assert.Equal(StartupSource.RunMachine32, byName["Steam"].Source);
        Assert.Equal("Valve Corp.", byName["Steam"].Publisher.Name);
        Assert.Equal(ImpactLevel.High, byName["Steam"].Impact.Level);
        Assert.True(byName["Steam"].NeedsAdmin);

        Assert.True(byName["SecurityHealth"].IsMicrosoft);
        Assert.Equal(ImpactLevel.Unknown, byName["SecurityHealth"].Impact.Level);

        Assert.Equal(StartupSource.RunOnceUser, byName["Setup"].Source);
        Assert.False(byName["Setup"].CanToggle);                      // RunOnce: só leitura

        Assert.Equal(@"C:\Tools\Macro\macro.exe", byName["Macro"].ExecutablePath);
        Assert.Equal("Macro.lnk", byName["Macro"].EntryName);
        Assert.DoesNotContain("desktop", byName.Keys);

        Assert.True(byName["LGHUB Updater"].CanToggle);
        Assert.False(byName["LGHUB Updater"].NeedsAdmin);             // tarefa da própria conta, sem elevação
        Assert.DoesNotContain("Backup diário", byName.Keys);         // sem gatilho de logon/boot
        Assert.True(byName["Y"].IsMicrosoft);
        Assert.False(byName["Y"].CanToggle);                          // tarefa do Windows: só leitura

        Assert.Contains("Apple Mobile Device Service", byName.Keys);  // serviço automático de terceiro
        Assert.DoesNotContain("Cliente DNS", byName.Keys);            // Microsoft fica na página Serviços
        Assert.DoesNotContain("Plataforma de Dispositivos Conectados", byName.Keys); // por usuário
        Assert.DoesNotContain("Fax", byName.Keys);                    // não automático

        Assert.Contains(inv.Notes, n => n.Contains("comum", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("020000000000000000000000", true)]
    [InlineData("060000000000000000000000", true)]
    [InlineData("030000000000000000000000", false)]
    [InlineData("070000000000000000000000", false)]
    public void Approved_state_follows_task_manager(string? hex, bool enabled) => Assert.Equal(enabled, StartupInventory.IsApprovedEnabled(hex));

    [Fact]
    public void Task_xml_from_schtasks_is_parsed()
    {
        const string output = """
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \Atualizador -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Fornecedor</Author><URI>\Atualizador</URI></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger><TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary></TimeTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>PC\ana</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><Enabled>false</Enabled></Settings>
              <Actions Context="Author"><Exec><Command>"C:\Program Files\F\upd.exe"</Command><Arguments>/silent</Arguments></Exec></Actions>
            </Task>
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \Pasta\Boot -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><BootTrigger><Enabled>false</Enabled></BootTrigger></Triggers>
              <Actions><Exec><Command>C:\x.exe</Command></Exec></Actions>
            </Task>
            """;
        var tasks = TaskXml.ParseMany(output);
        Assert.Equal(2, tasks.Count);
        var a = tasks[0];
        Assert.Equal(@"\Atualizador", a.Path);
        Assert.False(a.Enabled);
        Assert.True(a.RunsElevated);
        Assert.True(a.IsStartupTask);
        Assert.Equal(@"""C:\Program Files\F\upd.exe""", a.Command);
        Assert.Equal("/silent", a.Arguments);
        Assert.Equal(@"\Pasta\Boot", tasks[1].Path);
        Assert.False(tasks[1].IsStartupTask); // gatilho de boot desabilitado não conta
    }

    // ------------------------------------------------------------------ política de serviços

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("DcomLaunch")]
    [InlineData("LSM")]
    [InlineData("BFE")]
    [InlineData("MpsSvc")]
    [InlineData("WinDefend")]
    [InlineData("EventLog")]
    [InlineData("Winmgmt")]
    [InlineData("CryptSvc")]
    [InlineData("Dhcp")]
    [InlineData("Dnscache")]
    [InlineData("nsi")]
    [InlineData("AudioSrv")]
    [InlineData("vgk")]
    [InlineData("EasyAntiCheat")]
    [InlineData("rpcss")]
    public void Forbidden_services_are_never_changed(string service)
    {
        Assert.NotNull(ServicePolicy.Check(service, "Manual"));
        Assert.NotNull(ServicePolicy.Check(service, "Automatic"));
        Assert.True(ServicePolicy.IsProtected(service));
    }

    [Fact]
    public void Windows_update_may_go_manual_but_never_disabled()
    {
        Assert.Null(ServicePolicy.Check("wuauserv", "Manual"));
        Assert.NotNull(ServicePolicy.Check("wuauserv", "Disabled"));
        Assert.NotNull(ServicePolicy.Check("UsoSvc", "Disabled"));
        Assert.Null(ServicePolicy.Check("Fax", "Disabled"));
    }

    [Theory]
    [InlineData(0x10, true, false)]
    [InlineData(0x20, true, false)]
    [InlineData(0x110, true, false)] // interativo
    [InlineData(0x1, false, false)]  // driver de kernel
    [InlineData(0x2, false, false)]  // driver de sistema de arquivos
    [InlineData(0x50, true, true)]   // modelo por usuário
    [InlineData(0xE0, true, true)]   // instância por usuário
    public void Service_type_rules(int type, bool win32, bool perUser)
    {
        Assert.Equal(win32, ServicePolicy.IsWin32Service(type));
        Assert.Equal(perUser, ServicePolicy.IsPerUserService(type));
    }

    private static (BootServiceStartTypeHandler Handler, FakeServices Svc, InMemoryRegistryBackend Reg) ServiceHandler()
    {
        var svc = new FakeServices();
        var reg = new InMemoryRegistryBackend();
        return (new BootServiceStartTypeHandler(svc, reg), svc, reg);
    }

    private static void SeedType(InMemoryRegistryBackend reg, string service, int type) =>
        reg.Seed(new RegistryTarget(RegHive.HKLM, $@"SYSTEM\CurrentControlSet\Services\{service}", "Type", RegView.Default), "DWord",
            type.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("RpcSs", "Manual")]
    [InlineData("WinDefend", "Disabled")]
    [InlineData("wuauserv", "Disabled")]
    [InlineData("BFE", "Manual")]
    public void Individual_service_change_refuses_forbidden_targets(string service, string to)
    {
        var (handler, svc, _) = ServiceHandler();
        svc.Start[service] = "Automatic";
        var a = FromCatalog(ServicePresets.IndividualActionKey, to, new() { ["service"] = service });
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(a, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => handler.Apply(a));
        Assert.Equal(0, svc.Writes);
    }

    [Fact]
    public void Individual_service_change_refuses_drivers_and_per_user_services()
    {
        var (handler, svc, reg) = ServiceHandler();
        svc.Start["somedrv"] = "Manual";
        svc.Start["CDPUserSvc_1a2b"] = "Automatic";
        SeedType(reg, "somedrv", 0x1);
        SeedType(reg, "CDPUserSvc_1a2b", 0xE0);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(FromCatalog(ServicePresets.IndividualActionKey, "Disabled", new() { ["service"] = "somedrv" }), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(FromCatalog(ServicePresets.IndividualActionKey, "Manual", new() { ["service"] = "CDPUserSvc_1a2b" }), T.Ctx).Kind);
    }

    [Theory]
    [InlineData("Fax; rm -rf")]
    [InlineData("..\\x")]
    [InlineData(" Fax")]
    [InlineData("a/b")]
    public void Service_name_parameter_is_strict(string name)
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == ServicePresets.IndividualActionKey);
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["service"] = name }, out _));
    }

    [Fact]
    public void Service_change_applies_and_restores_delayed_automatic()
    {
        var (handler, svc, reg) = ServiceHandler();
        svc.Start["MapsBroker"] = "AutomaticDelayed";
        SeedType(reg, "MapsBroker", 0x20);
        var (journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("p", "1");
        var a = FromCatalog("boot.service.mapsbroker.manual@1", null);
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal("Manual", svc.Start["MapsBroker"]);
        exec.RevertRun(run);
        Assert.Equal("AutomaticDelayed", svc.Start["MapsBroker"]);
    }

    // ------------------------------------------------------------------ tarefas agendadas

    [Fact]
    public void Scheduled_task_disable_and_restore()
    {
        var tasks = new FakeTasks();
        tasks.Tasks[@"\LGHUB Updater"] = new ScheduledTaskInfo(@"\LGHUB Updater", true, [TaskTriggerKind.Logon], @"C:\l.exe", null, null, @"PC\ana", false);
        var handler = new BootScheduledTaskHandler(tasks);
        var (journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("p", "1");
        var a = FromCatalog("boot.task.user@1", "disabled", new() { ["taskPath"] = @"\LGHUB Updater" });
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.False(tasks.Tasks[@"\LGHUB Updater"].Enabled);
        exec.RevertRun(run);
        Assert.True(tasks.Tasks[@"\LGHUB Updater"].Enabled);
    }

    [Fact]
    public void Scheduled_task_rules_refuse_windows_tasks_and_non_startup_tasks()
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == "boot.task.admin@1");
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["taskPath"] = @"\Microsoft\Windows\Defrag\ScheduledDefrag" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["taskPath"] = @"\MICROSOFT\x" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["taskPath"] = "sem barra" }, out _));
        Assert.NotNull(entry.ValidateUserArguments(new Dictionary<string, string> { ["taskPath"] = @"\Fornecedor\Atualizar ção" }, out _));

        var tasks = new FakeTasks();
        tasks.Tasks[@"\Diária"] = new ScheduledTaskInfo(@"\Diária", true, [TaskTriggerKind.Other], @"C:\d.exe", null, null, null, false);
        var handler = new BootScheduledTaskHandler(tasks);
        var daily = FromCatalog("boot.task.admin@1", "disabled", new() { ["taskPath"] = @"\Diária" });
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(daily, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.NotApplicable, handler.Assess(FromCatalog("boot.task.admin@1", "disabled", new() { ["taskPath"] = @"\Sumiu" }), T.Ctx).Kind);
        // Mesmo que alguém monte a ação à mão, o handler recusa \Microsoft\ e ".." .
        var forged = daily with { Target = new Dictionary<string, string> { ["taskPath"] = @"\Microsoft\Windows\X" } };
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(forged, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => handler.Apply(forged));
        Assert.False(BootScheduledTaskHandler.IsValidPath(@"\a\..\b"));
        Assert.NotNull(handler.CheckRestore(daily, StateSnapshot.Absent()));
    }

    // ------------------------------------------------------------------ pasta Inicializar

    [Fact]
    public void Startup_folder_item_uses_task_manager_flag_and_restores_absent()
    {
        var reg = new InMemoryRegistryBackend();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\ana\Startup\Macro.lnk" };
        var handler = new BootStartupFolderHandler(reg, (scope, sid) => scope == RegHive.HKCU && sid is null ? @"C:\Users\ana\Startup" : null, files.Contains);
        var (journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("p", "1");
        var a = FromCatalog("boot.startupfolder.user@1", "disabled", new() { ["fileName"] = "Macro.lnk" });
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        var flag = new RegistryTarget(RegHive.HKCU, StartupInventory.ApprovedPath + @"\StartupFolder", "Macro.lnk", RegView.Default);
        Assert.StartsWith("03", reg.Read(flag).Value, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\ana\Startup\Macro.lnk", files); // o atalho não é tocado
        exec.RevertRun(run);
        Assert.False(reg.Read(flag).ValueExists);

        var missing = FromCatalog("boot.startupfolder.user@1", "disabled", new() { ["fileName"] = "Nada.lnk" });
        Assert.Equal(ApplicabilityKind.NotApplicable, handler.Assess(missing, T.Ctx).Kind);
        var entry = Catalog().Actions.Single(x => x.Definition.Key == "boot.startupfolder.user@1");
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["fileName"] = @"..\..\Windows\x.lnk" }, out _));
    }

    // ------------------------------------------------------------------ entradas órfãs

    private static (BootOrphanEntryHandler Handler, InMemoryRegistryBackend Reg, HashSet<string> Files) Orphans(bool otherAccount = false)
    {
        var reg = new InMemoryRegistryBackend();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (new BootOrphanEntryHandler(reg, files.Contains, Env, otherAccount), reg, files);
    }

    private static RegistryTarget RunValue(RegHive hive, string name, RegView view = RegView.Registry64, string? sid = null) =>
        new(hive, StartupInventory.RunPath, name, view, sid);

    [Fact]
    public void Orphan_entry_is_removed_with_full_backup_and_restored_exactly()
    {
        var (handler, reg, _) = Orphans();
        var target = RunValue(RegHive.HKCU, "Velho");
        reg.Seed(target, "ExpandString", @"""%ProgramFiles%\Velho\velho.exe"" /min");
        var (journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("p", "1");
        var a = FromCatalog("boot.orphan.user.run@1", null, new() { ["entryName"] = "Velho" });
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.False(reg.Read(target).ValueExists);
        exec.RevertRun(run);
        var back = reg.Read(target);
        Assert.Equal("ExpandString", back.Kind);
        Assert.Equal(@"""%ProgramFiles%\Velho\velho.exe"" /min", back.Value);
    }

    [Fact]
    public void Orphan_removal_refuses_whenever_absence_is_not_proven()
    {
        var (handler, reg, files) = Orphans();
        files.Add(@"C:\Program Files\Vivo\vivo.exe");
        reg.Seed(RunValue(RegHive.HKCU, "Vivo"), "String", @"C:\Program Files\Vivo\vivo.exe");
        reg.Seed(RunValue(RegHive.HKCU, "Var"), "String", @"%NAOEXISTE%\x.exe");
        reg.Seed(RunValue(RegHive.HKCU, "Rel"), "String", "x.exe /q");
        reg.Seed(RunValue(RegHive.HKCU, "Rede"), "String", @"\\nas\apps\x.exe");
        foreach (var name in new[] { "Vivo", "Var", "Rel", "Rede" })
        {
            var a = FromCatalog("boot.orphan.user.run@1", null, new() { ["entryName"] = name });
            Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(a, T.Ctx).Kind);
            Assert.Throws<InvalidOperationException>(() => handler.Apply(a));
            Assert.True(reg.Read(RunValue(RegHive.HKCU, name)).ValueExists);
        }
    }

    [Fact]
    public void Orphan_check_for_another_account_uses_its_profile_and_machine_variables_only()
    {
        var (handler, reg, files) = Orphans(otherAccount: true);
        reg.Seed(new RegistryTarget(RegHive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{Sid}", "ProfileImagePath", RegView.Default),
            "ExpandString", @"C:\Users\bia");
        reg.Seed(RunValue(RegHive.HKCU, "App", sid: Sid), "String", @"%LOCALAPPDATA%\App\app.exe");
        reg.Seed(RunValue(RegHive.HKCU, "Temp"), "String", @"%TEMP%\x.exe");
        var a = UserProfileScope.Bind(FromCatalog("boot.orphan.user.run@1", null, new() { ["entryName"] = "App" }), Sid);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(a, T.Ctx).Kind);       // C:\Users\bia\AppData\Local\App\app.exe não existe
        files.Add(@"C:\Users\bia\AppData\Local\App\app.exe");
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(a, T.Ctx).Kind);  // agora existe: não é órfã
        // Sem SID e rodando como outra conta: variável de usuário não é resolvida.
        var noSid = FromCatalog("boot.orphan.user.run@1", null, new() { ["entryName"] = "Temp" });
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(noSid, T.Ctx).Kind);
    }

    // ------------------------------------------------------------------ catálogos

    [Fact]
    public void Module_catalogs_load_and_every_handler_kind_is_registered()
    {
        var all = Catalog();
        var mine = all.Actions.Where(a => a.Definition.Id.StartsWith("boot.", StringComparison.Ordinal)).ToList();
        Assert.True(mine.Count >= 20);
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, false);
        var kinds = BootHandlers.Create(env).Select(h => h.HandlerKind).ToHashSet(StringComparer.Ordinal);
        kinds.Add(Nexus.WindowsActions.Startup.StartupApprovedHandler.Kind); // reaproveitado (registrado pelo broker)
        foreach (var a in mine)
        {
            Assert.Contains(a.Definition.HandlerKind, kinds);
            Assert.NotEmpty(a.Definition.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(a.Definition.Mechanism));
            Assert.False(string.IsNullOrWhiteSpace(a.Definition.Risks));
            Assert.False(string.IsNullOrWhiteSpace(a.Revert));
        }
        // Handlers novos têm prefixo do módulo e nomes únicos.
        Assert.All(BootHandlers.Create(env), h => Assert.StartsWith("Boot", h.HandlerKind, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_user_parameter_action_instantiates_with_valid_arguments()
    {
        var samples = new Dictionary<string, string> { ["entryName"] = "Steam", ["fileName"] = "Macro.lnk", ["taskPath"] = @"\Fornecedor\Atualizar", ["service"] = "Fax" };
        foreach (var a in Catalog().Actions.Where(a => a.Definition.Id.StartsWith("boot.", StringComparison.Ordinal)))
        {
            var args = a.UserParameters.Keys.ToDictionary(k => k, k => samples[k]);
            var value = a.DesiredValue is null ? a.Definition.AllowedValues![0] : null;
            Assert.NotNull(FromCatalog(a.Definition.Key, value, args));
        }
    }

    [Fact]
    public void Presets_point_to_catalog_actions_and_never_touch_protected_services()
    {
        var presets = ServicePresets.Load(Path.Combine(AppContext.BaseDirectory, "catalog"));
        Assert.True(presets.Count >= 4);
        var catalog = Catalog();
        var (handler, svc, _) = ServiceHandler();
        foreach (var item in presets.SelectMany(p => p.Items))
        {
            var entry = catalog.Actions.SingleOrDefault(a => a.Definition.Key == item.ActionKey);
            Assert.NotNull(entry);
            Assert.Equal(BootServiceStartTypeHandler.Kind, entry!.Definition.HandlerKind);
            Assert.Equal(item.Service, entry.TargetTemplate["service"]);
            Assert.Equal(item.StartType, entry.DesiredValue);
            Assert.Null(ServicePolicy.Check(item.Service, item.StartType));
            svc.Start[item.Service] = "Automatic";
            Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(FromCatalog(item.ActionKey, null), T.Ctx).Kind);
        }
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Honesty)));
        Assert.DoesNotContain(presets.SelectMany(p => p.Items), i => i.Service.Equals("XboxGipSvc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Startup_entry_parameter_rejects_control_characters_and_backslash()
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == "boot.startup.user.run@1");
        Assert.NotNull(entry.ValidateUserArguments(new Dictionary<string, string> { ["entryName"] = "Discord (atualizador)" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["entryName"] = "a\\b" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["entryName"] = "a\nb" }, out _));
    }

    [Fact]
    public void Existing_startup_approved_handler_works_from_the_module_catalog()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(new RegistryTarget(RegHive.HKLM, StartupInventory.RunPath, "Steam", RegView.Registry32), "String", @"""C:\Program Files\Steam\steam.exe"" -silent");
        var (journal, exec, resolver) = Engine(new Nexus.WindowsActions.Startup.StartupApprovedHandler(reg));
        var run = journal.BeginRun("p", "1");
        var a = FromCatalog("boot.startup.machine.run32@1", "disabled", new() { ["entryName"] = "Steam" });
        Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges);
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        var flag = new RegistryTarget(RegHive.HKLM, StartupInventory.ApprovedPath + @"\Run32", "Steam", RegView.Default);
        Assert.StartsWith("03", reg.Read(flag).Value, StringComparison.Ordinal);
        exec.RevertRun(run);
        Assert.False(reg.Read(flag).ValueExists);
    }
}
