namespace Nexus.HardwareDiscovery;

/// <summary>Ponto de entrada multiplataforma: fora do Windows, tudo é "não suportado neste ambiente".</summary>
public static class Discovery
{
    public static SystemSnapshot Capture()
    {
        if (OperatingSystem.IsWindows())
            return WindowsDiscovery.Capture();

        var reason = $"Não suportado neste ambiente ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}); a descoberta usa APIs do Windows.";
        return new SystemSnapshot(DateTimeOffset.Now,
            [new DiscoverySection("Ambiente", [Fact.Unavailable("Descoberta de hardware", "Discovery", reason)])],
            new SystemFacts(), TimeSpan.Zero);
    }
}
