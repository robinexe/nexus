using Nexus.Assistant;
using Nexus.Broker;
using Nexus.Core;
using Nexus.DriverUpdates;

namespace Nexus.Tests.Modules;

/// <summary>"Ver se falta algum driver" (dispositivos com código de problema) e as correções apontadas pelo teste de ponta a ponta.</summary>
[Trait("Category", "Unit")]
public sealed class DeviceProblemsTests
{
    private static DeviceDriver Dev(string name, int? code, string? hw = @"PCI\VEN_8086&DEV_A0E8", string? inf = null, string? version = null) =>
        new(@"PCI\VEN_8086&DEV_A0E8\3&11583659&0&A8", name, hw is null ? [] : [hw], [], "Unknown", null, null, version, null, null, inf, null, code);

    [Fact]
    public void Only_devices_with_a_problem_code_are_listed_missing_driver_first()
    {
        var list = DeviceProblems.From(
        [
            Dev("Funcionando", 0),
            Dev("Sem leitura", null),
            Dev("Desativado", 22),
            Dev("Controlador de barramento SM", 28),
            Dev("Rede", 10),
            Dev("Coprocessador", 1),
        ]);
        Assert.Equal(["Controlador de barramento SM", "Coprocessador", "Rede", "Desativado"], list.Select(p => p.Name));
        Assert.Equal(DeviceProblemKind.MissingDriver, list[0].Kind);
        Assert.Equal("Código 28", list[0].CodeText);
        Assert.Equal("Os drivers não estão instalados.", list[0].Meaning);
        Assert.Equal(@"PCI\VEN_8086&DEV_A0E8", list[0].HardwareId);
        Assert.Equal(DeviceProblemKind.DriverFailed, list[2].Kind);
        Assert.Equal(DeviceProblemKind.Disabled, list[3].Kind);
        Assert.Equal(3, DeviceProblems.NeedingDriver(list).Count);
    }

    [Theory]
    [InlineData(28, DeviceProblemKind.MissingDriver)]
    [InlineData(18, DeviceProblemKind.MissingDriver)]
    [InlineData(1, DeviceProblemKind.MissingDriver)]
    [InlineData(31, DeviceProblemKind.DriverFailed)]
    [InlineData(39, DeviceProblemKind.DriverFailed)]
    [InlineData(43, DeviceProblemKind.DriverFailed)]
    [InlineData(52, DeviceProblemKind.DriverFailed)]
    [InlineData(22, DeviceProblemKind.Disabled)]
    [InlineData(29, DeviceProblemKind.Disabled)]
    [InlineData(14, DeviceProblemKind.Other)]
    [InlineData(999, DeviceProblemKind.Other)]
    public void Codes_are_grouped_as_the_device_manager_documents(int code, DeviceProblemKind kind)
    {
        Assert.Equal(kind, DeviceProblems.KindOf(code));
        Assert.False(string.IsNullOrWhiteSpace(DeviceProblems.MeaningOf(code)));
    }

    [Fact]
    public void Device_without_hardware_id_or_name_still_shows()
    {
        var p = Assert.Single(DeviceProblems.From([Dev(" ", 28, hw: null)]));
        Assert.Equal(@"PCI\VEN_8086&DEV_A0E8\3&11583659&0&A8", p.Name);
        Assert.Null(p.HardwareId);
    }

    [Fact]
    public void Reading_exposes_devices_needing_driver()
    {
        var r = new DriverUpdatesReading([], WuErrorKind.None, null, false, [], null, null)
        {
            DeviceCount = 3,
            ProblemCodesRead = true,
            Problems = DeviceProblems.From([Dev("A", 28), Dev("B", 22)]),
        };
        Assert.Equal("A", Assert.Single(r.NeedingDriver).Name);
    }

    [Fact]
    public void Assistant_drivers_step_reports_devices_without_driver_and_tells_the_user_what_to_do()
    {
        var reading = new DriverUpdatesReading([], WuErrorKind.None, null, false, [], null, null)
        {
            DeviceCount = 40,
            ProblemCodesRead = true,
            Problems = DeviceProblems.From([Dev("Controlador de barramento SM", 28)]),
        };
        var scan = StepPlanners.Drivers(AssistantFakes.Cat(), reading, searched: true);
        var fact = Assert.Single(scan.Facts, f => f.Label == "Dispositivos sem driver");
        Assert.Equal("1", fact.Value);
        Assert.Equal("bad", fact.Tone);
        Assert.Contains("Código 28", fact.Detail, StringComparison.Ordinal);
        var action = Assert.Single(scan.UserActions);
        Assert.Contains("Controlador de barramento SM", action.Title, StringComparison.Ordinal);
        Assert.Equal(DeviceProblems.Source, action.Link);

        var clean = new DriverUpdatesReading([], WuErrorKind.None, null, false, [], null, null) { DeviceCount = 40, ProblemCodesRead = true };
        var none = StepPlanners.Drivers(AssistantFakes.Cat(), clean, searched: true);
        Assert.Equal("good", Assert.Single(none.Facts, f => f.Label == "Dispositivos sem driver").Tone);
        Assert.Empty(none.UserActions);
    }

    [Fact]
    public void Assistant_never_proposes_a_session_action_because_it_would_lock_the_broker_for_the_next_steps()
    {
        const string catalog = """
        {
          "catalogVersion": "t", "schema": "nexus.catalog/1",
          "actions": [
            { "id": "t.session", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User", "persistence": "Session",
              "target": { "hive": "HKCU", "path": "Software\\T", "name": "S", "type": "DWord" }, "desiredValue": 1 },
            { "id": "t.persistent", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User",
              "target": { "hive": "HKCU", "path": "Software\\T", "name": "P", "type": "DWord" }, "desiredValue": 1 }
          ]
        }
        """;
        var cat = new AssistantCatalog(CatalogLoader.Parse(catalog));
        Assert.Null(cat.Change(AssistantStepId.Performance, new ActionRequest("t.session@1"), "s", "d"));
        Assert.NotNull(cat.Change(AssistantStepId.Performance, new ActionRequest("t.persistent@1"), "p", "d"));
    }

    [Fact]
    public void Real_catalog_session_actions_are_never_proposed_by_the_assistant()
    {
        var cat = AssistantFakes.Real();
        var sessions = cat.Document.Actions.Where(a => a.Definition.Persistence == PersistenceKind.Session).ToList();
        Assert.NotEmpty(sessions);
        foreach (var s in sessions)
            Assert.Null(cat.Change(AssistantStepId.Performance, new ActionRequest(s.Definition.Key), "s", "d"));
        Assert.Equal(BrokerLevel.Admin, cat.Change(AssistantStepId.Welcome, new ActionRequest(StepPlanners.RestorePointAction), "r", "d")!.Level);
    }
}
