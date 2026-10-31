using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.Privacy;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>Privacidade com o registro REAL, pelo BrokerService e o catálogo real. Só roda numa VM liberada (portão antes de tudo).</summary>
[Trait("Category", "VmIntegration")]
public sealed class PrivacyIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly WindowsRegistryBackend _reg = new();
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public PrivacyIntegrationTests()
    {
        Vm.Require(); // nada é criado antes do portão
        _dir = Directory.CreateTempSubdirectory("nexus-vm-privacy-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private (BrokerService Broker, SqliteJournal Journal) Broker()
    {
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(_reg));
        foreach (var h in PrivacyHandlers.Create(new HandlerEnvironment(_reg, _dir, OtherAccount: false, Elevated: Vm.Elevated))) handlers.Add(h);
        var journal = new SqliteJournal(JournalPath);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return (new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>()), journal);
    }

    private static string? Read(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    [Fact]
    public void User_and_policy_items_are_applied_verified_and_reverted_exactly()
    {
        const string langPath = @"Control Panel\International\User Profile";
        const string telPath = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
        var langBefore = Read(Registry.CurrentUser, langPath, "HttpAcceptLanguageOptOut");
        var telBefore = Read(Registry.LocalMachine, telPath, "AllowTelemetry");

        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var actions = new List<ActionRequest> { new("privacy.language-list.optout@1") };
            if (Vm.Elevated) actions.Add(new("privacy.telemetry.required@1"));
            var exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, actions));
            Assert.True(exec.Ok, exec.Error?.Detail);
            Assert.All(exec.Results!, r => Assert.True(r.Outcome is "Verified" or "AlreadyConfigured", $"{r.ActionKey}: {r.Outcome} {r.Detail}"));
            Assert.Equal("1", Read(Registry.CurrentUser, langPath, "HttpAcceptLanguageOptOut"));
            if (Vm.Elevated) Assert.Equal("1", Read(Registry.LocalMachine, telPath, "AllowTelemetry"));

            // A página lê o mesmo estado que o aplicador escreveu.
            var states = new PrivacyScanner(_reg).Scan(PrivacyCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "catalog")), Vm.Facts.WindowsBuild ?? 0);
            Assert.Equal(PrivacyItemStatus.Protected, states.Single(s => s.Item.Id == "privacy.item.language").Status);

            var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
            Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
            Assert.Equal(langBefore, Read(Registry.CurrentUser, langPath, "HttpAcceptLanguageOptOut"));
            Assert.Equal(telBefore, Read(Registry.LocalMachine, telPath, "AllowTelemetry"));
        }
    }

    [Fact]
    public void Policy_outside_the_allowlist_is_refused_without_writing()
    {
        var before = Read(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware");
        var h = new PrivacyPolicyValueHandler(_reg);
        var a = new ActionInstance(
            new ActionDefinition("privacy.vm.forbidden", 1, "proibido", Classification.SupportedDocumented, PrivacyPolicyValueHandler.Kind, "teste",
                [new EvidenceRef(EvidenceLevel.Hypothesis, "teste")], "nenhum", PrivilegeLevel.Admin, PersistenceKind.Persistent, RebootRequirement.None),
            new Dictionary<string, string> { ["hive"] = "HKLM", ["path"] = @"SOFTWARE\Policies\Microsoft\Windows Defender", ["name"] = "DisableAntiSpyware", ["type"] = "DWord" },
            StateSnapshot.Of("DWord", "1"));
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a, new SystemContext(26100, "?", null, true)).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(a));
        Assert.Equal(before, Read(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware"));
    }
}
