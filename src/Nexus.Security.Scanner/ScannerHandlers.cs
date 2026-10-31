using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Security.Scanner;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class ScannerHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows())
            return [new ScannerQuarantineHandler(new WindowsQuarantineLocations(environment.OtherAccount), environment.DataDirectory,
                environment.Elevated, environment.OtherAccount, AdminOwnedFile.Check)];
        // Fora do Windows (testes do catálogo): o handler existe, mas a política recusa tudo (pastas indeterminadas).
        return [new ScannerQuarantineHandler(new UnknownLocations(), environment.DataDirectory, environment.Elevated, environment.OtherAccount)];
    }

    /// <summary>Com pastas explícitas (testes).</summary>
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment, IQuarantineLocations locations, bool forceCrossVolume = false)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return [new ScannerQuarantineHandler(locations, environment.DataDirectory, environment.Elevated, environment.OtherAccount, forceCrossVolume: forceCrossVolume)];
    }

    private sealed class UnknownLocations : IQuarantineLocations
    {
        public string? WindowsDirectory => "/";
        public IReadOnlyList<string> AdminOnlyRoots => [];
        public string? UsersRoot => null;
        public string? UserProfile(string? userSid) => null;
    }
}
