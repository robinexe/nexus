using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nexus.Broker;
using Nexus.Core;
using Nexus.DriverUpdates;
using Nexus.DriverUpdates.Windows;

namespace Nexus.IntegrationTests.E2E;

/// <summary>
/// Atualizar drivers de ponta a ponta num Windows de verdade (VM descartável do CI).
/// <list type="number">
/// <item>Busca REAL no Windows Update (Type='Driver'); se houver oferta instalável, instala UMA (opcional de preferência) pelo
/// aplicador elevado real, confere a versão nova no dispositivo, desfaz e confere a volta. VM do CI quase nunca tem oferta: o
/// teste registra isso (arquivo drivers-e2e.json) e passa — só um PC real com oferta prova a instalação pelo WUA.</item>
/// <item>Backup e restauração do handler com um driver REAL do PC: exporta o pacote atual (pnputil /export-driver), grava o
/// manifesto, "instala" por um Windows Update falso (a única peça falsa) e executa o Desfazer real (pnputil /add-driver +
/// UpdateDriverForPlugAndPlayDevices com INSTALLFLAG_FORCE) num dispositivo de classe inofensiva, conferindo que o
/// dispositivo continua com o mesmo driver e sem código de problema.</item>
/// <item>"Ver se falta algum": código de problema de todos os dispositivos (CM_Get_DevNode_Status) lido e listado.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ESerial.Name)]
[SupportedOSPlatform("windows")]
public sealed class DriverUpdatesEndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Classes em que reinstalar o driver não derruba a VM (nada de rede, disco, controlador, sistema ou vídeo).</summary>
    private static readonly string[] SafeClasses = ["PrintQueue", "Monitor", "SoftwareDevice", "Printer", "Mouse", "HIDClass", "Keyboard", "Image", "MEDIA", "AudioEndpoint"];

    /// <summary>Classes em que uma instalação de driver do Windows Update pode derrubar a VM do CI (rede, disco, barramento).</summary>
    private static readonly string[] UnsafeClasses = ["Net", "DiskDrive", "SCSIAdapter", "HDC", "Volume", "System", "Processor", "Computer", "Firmware", "hdc", "USB"];

    private static ActionInstance Install(string updateId, int revision) => new(
        new ActionDefinition("driverupdates.install", 1, "t", Classification.SupportedDocumented, DriverUpdateInstallHandler.Kind, "", [], "",
            PrivilegeLevel.Admin, PersistenceKind.Persistent, RebootRequirement.Recommended),
        new Dictionary<string, string> { ["updateId"] = updateId, ["revision"] = revision.ToString(CultureInfo.InvariantCulture) },
        StateSnapshot.Of(DriverUpdateInstallHandler.StateType, DriverUpdateInstallHandler.Installed));

    // ==================================================================== 1. Windows Update real

    [WindowsE2EFact]
    public void Windows_Update_search_and_one_real_install_with_undo_when_an_offer_exists()
    {
        Vm.Require();
        using var log = new E2ELog(output, "drivers-wu-e2e.log");
        var devices = new SetupApiDeviceInventory().List();
        var search = new WuaAgent().SearchDrivers(TimeSpan.FromMinutes(8), CancellationToken.None);
        log.Line(search.Ok ? $"Windows Update: {search.Updates.Count} oferta(s) de driver" : $"Windows Update falhou: {search.Error}: {search.Message}");
        var offers = search.Ok ? DriverUpdatesReader.Combine(search.Updates, devices) : [];
        foreach (var o in offers)
            log.Line($"    {(o.Update.Optional ? "opcional   " : "recomendada")} {o.Device} · {o.Update.Title} · {o.VersionLine} · classe {o.Update.DriverClass} · hw {o.Update.HardwareId} · desfazer {o.Rollback.Source}{(o.Blocked is null ? "" : " · BLOQUEADA: " + o.Blocked)}");
        var summary = new Dictionary<string, object?>
        {
            ["busca"] = search.Ok ? "ok" : $"{search.Error}: {search.Message}",
            ["ofertas"] = offers.Select(o => new { o.Device, o.Update.Title, o.VersionLine, optional = o.Update.Optional, o.Blocked, rollback = o.Rollback.Source.ToString() }).ToList(),
        };
        try
        {
            if (!search.Ok)
            {
                // Sem internet/serviço: a mensagem precisa ser clara (é o que a página mostra). Não é defeito do NEXUS.
                Assert.False(string.IsNullOrWhiteSpace(search.Message));
                summary["resultado"] = "busca sem resultado; instalação real NÃO provada nesta VM";
                return;
            }
            var pick = offers
                .Where(o => o.Blocked is null && o.Rollback.Possible)
                .Where(o => !UnsafeClasses.Contains(o.Update.DriverClass ?? "", StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(o => o.Update.Optional)
                .ThenBy(o => o.Update.SizeBytes)
                .FirstOrDefault();
            if (pick is null)
            {
                log.Line("Nenhuma oferta instalável com segurança nesta VM (comum em VM do Azure). Instalação real pelo WUA NÃO provada aqui; o caminho de backup/restauração é provado no outro teste.");
                summary["resultado"] = "sem oferta instalável; instalação real NÃO provada nesta VM";
                return;
            }

            var deviceBefore = devices.First(d => DeviceMatching.Match(pick.Update, [d]).Count > 0 && d.InfName is not null);
            log.Line($"Instalando pelo aplicador elevado real: {pick.Update.Title} em {deviceBefore.Description} (antes {deviceBefore.DriverVersion}, {deviceBefore.InfName})");
            var session = BrokerSession.ForThisPc(BrokerLevel.Admin, AppContext.BaseDirectory, BrokerTestHost.Sid, m => log.Line("[aplicador] " + m));
            var exec = session.Execute([new ActionRequest("driverupdates.install@1", Arguments: new Dictionary<string, string>
            {
                ["updateId"] = pick.Update.UpdateId,
                ["updateRevision"] = pick.Update.Revision.ToString(CultureInfo.InvariantCulture),
            })], "modulo");
            log.Line(exec.Message);
            foreach (var x in exec.Response?.Results ?? []) log.Line($"    {x.Outcome} {x.Detail}");
            Assert.True(exec.Ok && exec.Response?.RunId is not null, exec.Message);
            var result = Assert.Single(exec.Response!.Results!);
            Assert.Equal("Verified", result.Outcome);
            var reboot = WindowsRebootState.Pending(new WuaAgent()) == true;
            var deviceAfter = new SetupApiDeviceInventory().List().First(d => d.InstanceId == deviceBefore.InstanceId);
            log.Line($"Depois: {deviceAfter.DriverVersion} ({deviceAfter.InfName}) · reinício pendente: {reboot}");
            if (!reboot) Assert.True(DeviceMatching.SameVersion(deviceAfter.DriverVersion, pick.Update.OfferedVersion), $"versão no dispositivo {deviceAfter.DriverVersion} ≠ oferecida {pick.Update.OfferedVersion}");
            Assert.True(Directory.Exists(DriverUpdateInstallHandler.BackupRoot(Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!)), "pasta de backup dos drivers");

            var revert = session.RevertRun(exec.Response.RunId!.Value);
            log.Line(revert.Message);
            foreach (var x in revert.Response?.Results ?? []) log.Line($"    {x.Outcome} {x.Detail}");
            Assert.True(revert.Ok && revert.Response?.FullyRestored == true, revert.Message);
            var deviceBack = new SetupApiDeviceInventory().List().First(d => d.InstanceId == deviceBefore.InstanceId);
            log.Line($"Depois de desfazer: {deviceBack.DriverVersion} ({deviceBack.InfName}), código de problema {deviceBack.ProblemCode}");
            if (!reboot) Assert.True(DeviceMatching.SameVersion(deviceBack.DriverVersion, deviceBefore.DriverVersion), $"não voltou: {deviceBack.DriverVersion} ≠ {deviceBefore.DriverVersion}");
            summary["resultado"] = $"instalada e desfeita: {pick.Update.Title} ({deviceBefore.DriverVersion} → {deviceAfter.DriverVersion} → {deviceBack.DriverVersion})";
        }
        finally
        {
            File.WriteAllText(Path.Combine(log.Directory, "drivers-wu-e2e.json"), JsonSerializer.Serialize(summary, Json));
        }
    }

    // ==================================================================== 2. backup e restauração com driver real

    /// <summary>Windows Update falso que "oferece" uma versão nova para um dispositivo real e "instala" sem mudar nada.</summary>
    private sealed class OfferFor(DeviceDriver device, string offered) : IWindowsUpdateAgent
    {
        public const string Id = "0e2e0e2e-0e2e-4e2e-8e2e-0e2e0e2e0e2e";
        public int Installs { get; private set; }
        public WuSearchOutcome SearchDrivers(TimeSpan timeout, CancellationToken cancel) => WuSearchOutcome.Success(
        [
            new WuDriverUpdate(Id, 1, $"E2E - {device.ClassName} - {offered}", IsDriver: true, Optional: true, EulaAccepted: true, IsDownloaded: true, 1024,
                device.ClassName, device.HardwareIds[0], device.Manufacturer, device.Description, device.DriverProvider, null, offered, [], null),
        ]);
        public WuInstallResult DownloadAndInstall(string updateId, int revision, TimeSpan timeout)
        {
            Installs++;
            return new WuInstallResult(2, false, 0, null);
        }
        public bool? RebootRequired() => false;
    }

    private static string Bump(string version)
    {
        var v = Version.Parse(version);
        return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision) + 1).ToString();
    }

    [WindowsE2EFact]
    public void Driver_backup_and_undo_path_runs_with_a_real_driver_package_and_pnputil()
    {
        Vm.Require();
        Assert.True(Vm.Elevated, "pnputil /export-driver e a instalação forçada precisam de administrador (a VM do CI é elevada).");
        using var log = new E2ELog(output, "drivers-backup-e2e.log");
        var inventory = new SetupApiDeviceInventory();
        var tool = new WindowsDriverPackageTool();
        var devices = inventory.List();
        log.Line($"{devices.Count} dispositivos; com driver de terceiros (oemNN.inf): {devices.Count(d => d.InfName?.StartsWith("oem", StringComparison.OrdinalIgnoreCase) == true)}");
        foreach (var g in devices.GroupBy(d => d.ClassName ?? "?").OrderBy(g => g.Key))
            log.Line($"    classe {g.Key}: {g.Count()} ({string.Join(", ", g.Select(d => d.InfName ?? "sem driver").Distinct().Take(4))})");
        var summary = new Dictionary<string, object?>();

        // a) Exportar QUALQUER pacote de terceiros (só lê o repositório; não toca no dispositivo) e achar o INF original no backup.
        var oem = devices.FirstOrDefault(d => d.InfName is { } i && i.StartsWith("oem", StringComparison.OrdinalIgnoreCase) && d.DriverVersion is not null);
        if (oem is not null)
        {
            var original = tool.OriginalInfName(oem.InfName!);
            var dir = Directory.CreateTempSubdirectory("nexus-e2e-export-").FullName;
            tool.Export(oem.InfName!, dir);
            var found = DriverUpdateInstallHandler.FindInf(dir, original ?? "");
            log.Line($"Export: {oem.InfName} ({original}) de {oem.Description} → {found ?? "INF NÃO ENCONTRADO"}");
            Assert.NotNull(original);
            Assert.NotNull(found);
            tool.Add(found!); // pacote já presente: só confere que o pnputil aceita guardar de novo (sem trocar o driver)
            summary["export"] = $"{oem.InfName} ({original}) exportado e aceito de volta pelo pnputil";
        }
        else
        {
            log.Line("Nenhum dispositivo com pacote de terceiros nesta VM: o export do pnputil não pôde ser exercitado (o caminho 'Inbox' é testado abaixo).");
            summary["export"] = "sem pacote de terceiros na VM";
        }

        // b) Ciclo completo do handler num dispositivo de classe inofensiva (terceiros = Backup, senão Inbox).
        var candidate = devices
            .Where(d => d.ProblemCode == 0 && d.HardwareIds.Count > 0 && d.DriverVersion is not null && Version.TryParse(d.DriverVersion, out _))
            .Where(d => SafeClasses.Contains(d.ClassName ?? "", StringComparer.OrdinalIgnoreCase))
            .Select(d => (Device: d, Plan: RollbackRules.Plan(d)))
            .Where(x => x.Plan.Possible)
            .OrderBy(x => x.Plan.Source == RollbackSource.Backup ? 0 : 1)
            .ThenBy(x => Array.FindIndex(SafeClasses, c => string.Equals(c, x.Device.ClassName, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (candidate.Device is null)
        {
            log.Line("Nenhum dispositivo de classe inofensiva com driver reinstalável nesta VM: ciclo de backup/restauração NÃO exercitado.");
            summary["ciclo"] = "sem dispositivo seguro";
            File.WriteAllText(Path.Combine(log.Directory, "drivers-backup-e2e.json"), JsonSerializer.Serialize(summary, Json));
            Assert.Fail("Nenhum dispositivo seguro para exercitar o Desfazer do driver (esperado na VM: fila de impressão ou monitor).");
        }
        var device = candidate.Device!;
        log.Line($"Dispositivo: {device.Description} · classe {device.ClassName} · {device.InfName} {device.DriverVersion} · id casado {device.MatchingDeviceId} · desfazer {candidate.Plan.Source}");

        var data = Directory.CreateTempSubdirectory("nexus-e2e-drvdata-").FullName;
        var wua = new OfferFor(device, Bump(device.DriverVersion!));
        var handler = new DriverUpdateInstallHandler(wua, inventory, tool, new WindowsBootClock(), data);
        var action = Install(OfferFor.Id, 1);
        var ctx = new SystemContext(Vm.Facts.WindowsBuild ?? 0, "vm", null, true);

        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, ctx).Kind);
        var original0 = handler.Detect(action);
        Assert.Equal(DriverUpdateInstallHandler.Pending, original0.Value);
        handler.Apply(action);
        Assert.Equal(1, wua.Installs);
        var manifest = handler.ReadManifest(OfferFor.Id);
        Assert.NotNull(manifest);
        log.Line($"Manifesto: origem {manifest!.Source}, INF {manifest.PreviousInf} ({manifest.PreviousOriginalInf}), versão {manifest.PreviousVersion}, id {manifest.PreviousMatchingId}, instalado {manifest.InstalledUtc:u}");
        Assert.Equal(candidate.Plan.Source, manifest.Source);
        if (manifest.Source == RollbackSource.Backup)
            Assert.NotNull(DriverUpdateInstallHandler.FindInf(handler.BackupDirectory(OfferFor.Id), manifest.PreviousOriginalInf!));
        Assert.Null(handler.CheckRestore(action, original0));

        handler.Restore(action, original0); // REAL: pnputil /add-driver (Backup) + UpdateDriverForPlugAndPlayDevices(FORCE)
        Thread.Sleep(3000);
        var back = inventory.List().FirstOrDefault(d => d.InstanceId == device.InstanceId);
        log.Line($"Depois do Desfazer: {back?.Description} · {back?.InfName} {back?.DriverVersion} · código de problema {back?.ProblemCode}");
        Assert.NotNull(back);
        Assert.True(DeviceMatching.SameVersion(back!.DriverVersion, device.DriverVersion), $"versão {back.DriverVersion} ≠ {device.DriverVersion}");
        Assert.Equal(0, back.ProblemCode ?? 0);
        summary["ciclo"] = $"{device.ClassName}: {candidate.Plan.Source} · export/manifesto/Desfazer real OK ({device.InfName} {device.DriverVersion})";
        File.WriteAllText(Path.Combine(log.Directory, "drivers-backup-e2e.json"), JsonSerializer.Serialize(summary, Json));
    }

    // ==================================================================== 3. ver se falta algum

    [WindowsE2EFact]
    public void Devices_without_driver_are_read_from_the_device_manager_problem_codes()
    {
        Vm.Require();
        using var log = new E2ELog(output, "drivers-faltando-e2e.log");
        var r = DriverUpdatesReader.Scan(TimeSpan.Zero, CancellationToken.None, searchUpdates: false);
        log.Line($"{r.DeviceCount} dispositivos · códigos lidos: {r.ProblemCodesRead} · com problema: {r.Problems.Count} · precisando de driver: {r.NeedingDriver.Count}");
        foreach (var p in r.Problems) log.Line($"    {p.Name} · {p.CodeText} · {p.Kind} · {p.Meaning} · {p.HardwareId}");
        Assert.True(r.DeviceCount > 0, "SetupAPI não listou dispositivos");
        Assert.True(r.ProblemCodesRead, "CM_Get_DevNode_Status não respondeu para nenhum dispositivo");
        Assert.All(r.Problems, p => Assert.True(p.Code > 0 && !string.IsNullOrWhiteSpace(p.Meaning)));
    }
}
