using Nexus.Core;
using Nexus.Programs.Inventory;
using Nexus.Programs.Leftovers;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Programs;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class ProgramsHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows()) return [CreateWindows(environment)];
        // Fora do Windows (testes do catálogo): o handler existe, mas qualquer acesso ao sistema falha sem efeito.
        return [new ProgramLeftoverHandler(new UnsupportedRegistryTree(), new UnsupportedRoots(), _ => throw new PlatformNotSupportedException(),
            environment.DataDirectory, environment.Elevated, environment.OtherAccount)];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ProgramLeftoverHandler CreateWindows(HandlerEnvironment environment)
    {
        var tree = new WindowsRegistryTreeBackend(requireUserProfileForHkcu: environment.OtherAccount);
        var otherAccount = environment.OtherAccount;
        return new ProgramLeftoverHandler(tree, new WindowsLeftoverRoots(tree, otherAccount), sid => InstalledFor(sid, otherAccount),
            environment.DataDirectory, environment.Elevated, otherAccount, AdminOwnedFile.Check);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<InstalledProgram> InstalledFor(string? sid, bool otherAccount) =>
        ProgramInventory.Build(new WindowsUninstallRegistrySource(sid, includeCurrentUser: !otherAccount).ReadAll());

    private sealed class UnsupportedRoots : ILeftoverRootsProvider
    {
        public IReadOnlyList<string> Machine() => [];
        public IReadOnlyList<string>? User(string? userSid) => null;
    }

    private sealed class UnsupportedRegistryTree : IRegistryTreeBackend
    {
        public IReadOnlyList<string> SubKeyNames(RegHive hive, string path) => throw new PlatformNotSupportedException();
        public bool KeyExists(RegHive hive, string path, string? userSid) => throw new PlatformNotSupportedException();
        public RegKeyTree? Export(RegHive hive, string path, string? userSid) => throw new PlatformNotSupportedException();
        public void DeleteTree(RegHive hive, string path, string? userSid) => throw new PlatformNotSupportedException();
        public void Import(RegHive hive, string path, RegKeyTree tree, string? userSid) => throw new PlatformNotSupportedException();
        public string? ReadMachineString(string path, string name) => throw new PlatformNotSupportedException();
    }
}
