using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// Broker instalado como SERVIÇO (LocalSystem) por tools\service\Install-Broker.ps1, numa VM liberada. Só roda quando a
/// variável NEXUS_SERVICE_TEST=1 indica que o serviço foi instalado e iniciado (o workflow do CI faz isso); fora disso,
/// retorna sem fazer nada. O cliente é este processo (usuário da VM): prova o achado B10 (HKCU = perfil de quem pede),
/// B3 (dono do pipe) e B7/R1 (ACL da trava e da pasta de dados).
/// </summary>
[Trait("Category", "ServiceIntegration")]
public sealed class ServiceIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private static bool Installed => Environment.GetEnvironmentVariable("NEXUS_SERVICE_TEST") == "1";
    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS");

    public ServiceIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string? Read(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    [Fact]
    public void Service_writes_to_the_callers_profile_records_the_sid_and_reverts()
    {
        if (!Installed) return;
        Vm.Require();
        const string path = @"Software\Microsoft\GameBar", name = "AutoGameModeEnabled";
        using var me = WindowsIdentity.GetCurrent();
        var mySid = me.User!.Value;
        var before = Read(Registry.CurrentUser, path, name);
        var systemBefore = Read(Registry.Users, @"S-1-5-18\" + path, name);
        _out.WriteLine($"[serviço] cliente={mySid}; HKCU antes={before ?? "(ausente)"}; HKU\\S-1-5-18 antes={systemBefore ?? "(ausente)"}");

        using var client = BrokerClient.Connect(BrokerPipeServer.DefaultPipeName, 15_000, BrokerServerKind.Service); // B3: dono do pipe
        var status = client.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Status)).Status!;
        _out.WriteLine($"[serviço] escrita liberada={status.ExecutionAllowed}: {status.ExecutionGateReason}");
        Assert.True(status.ExecutionAllowed, status.ExecutionGateReason);

        var exec = client.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest("win.gamemode.enable@1")]));
        Assert.True(exec.Ok, exec.Error?.Detail);
        var outcome = Assert.Single(exec.Results!).Outcome;
        _out.WriteLine($"[serviço] Execute: {outcome}; HKCU agora={Read(Registry.CurrentUser, path, name)}");
        Assert.Equal("Verified", outcome);                                       // o workflow garante que o valor inicial é outro
        Assert.Equal("1", Read(Registry.CurrentUser, path, name));                // no perfil de QUEM PEDIU
        Assert.Equal(systemBefore, Read(Registry.Users, @"S-1-5-18\" + path, name)); // não no do SYSTEM

        using (var journal = SqliteJournal.OpenReadOnly(Path.Combine(DataDir, "journal.db")))
        {
            var entry = Assert.Single(journal.Entries(exec.RunId!.Value));
            Assert.Equal(mySid, entry.Action.Target[UserProfileScope.TargetKey]);   // o perfil ficou no diário
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var revert = client.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
        Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
        Assert.Equal(before, Read(Registry.CurrentUser, path, name));
        Assert.Equal(systemBefore, Read(Registry.Users, @"S-1-5-18\" + path, name));
        _out.WriteLine("[serviço] RAMO: aplicado no HKCU do cliente, SID no diário, revertido; HKCU do SYSTEM intocado");
    }

    [Fact]
    public void Interface_path_applies_and_reverts_through_the_installed_service()
    {
        if (!Installed) return;
        Vm.Require();
        const string path = @"Software\Microsoft\GameBar", name = "AutoGameModeEnabled";
        var before = Read(Registry.CurrentUser, path, name);
        var session = BrokerSession.ForInstalledService(); // exatamente o que a interface usa

        var status = session.Status();
        _out.WriteLine($"[interface] {status.Message}");
        Assert.True(status.Ok, status.Message);
        Assert.Contains("LIBERADA", status.Message, StringComparison.Ordinal);

        var exec = session.Execute([new ActionRequest("win.gamemode.enable@1")]);
        _out.WriteLine($"[interface] {exec.Message}");
        Assert.True(exec.Ok, exec.Message);
        Assert.Equal("1", Read(Registry.CurrentUser, path, name));

        var revert = session.RevertRun(exec.Response!.RunId!.Value);
        _out.WriteLine($"[interface] {revert.Message}");
        Assert.True(revert.Ok && revert.Response!.FullyRestored == true, revert.Message);
        Assert.Equal(before, Read(Registry.CurrentUser, path, name));
    }

    [Fact]
    public void Installer_acls_keep_ordinary_users_away_from_the_lock_and_read_only_on_data()
    {
        if (!Installed) return;
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var nonAdmins = new[] { users, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                                new SecurityIdentifier(WellKnownSidType.WorldSid, null), new SecurityIdentifier(WellKnownSidType.InteractiveSid, null) };

        var lockAcl = new FileInfo(Path.Combine(DataDir, FileRunCoordinator.LockFileName)).GetAccessControl();
        var lockRules = lockAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        foreach (var r in lockRules) _out.WriteLine($"[trava] {r.IdentityReference} {r.AccessControlType} {r.FileSystemRights}");
        Assert.DoesNotContain(lockRules, r => r.AccessControlType == AccessControlType.Allow && nonAdmins.Contains((SecurityIdentifier)r.IdentityReference)); // B7

        var dataRules = new DirectoryInfo(DataDir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        foreach (var r in dataRules) _out.WriteLine($"[dados] {r.IdentityReference} {r.AccessControlType} {r.FileSystemRights}");
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        Assert.DoesNotContain(dataRules, r => r.AccessControlType == AccessControlType.Allow && nonAdmins.Contains((SecurityIdentifier)r.IdentityReference) && (r.FileSystemRights & write) != 0); // R1/B1
    }
}
