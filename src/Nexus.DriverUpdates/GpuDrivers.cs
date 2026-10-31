using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates;

/// <summary>App oficial do fabricante já instalado no PC (o NEXUS só abre; nunca baixa nem instala).</summary>
public sealed record VendorApp(string Name, string Path);

/// <summary>Estado do driver de uma placa de vídeo (lido do Laboratório de GPU, sem editar aquele módulo).</summary>
public sealed record GpuDriverStatus(
    string Name,
    GpuVendor Vendor,
    bool Integrated,
    string WindowsVersion,
    string? VendorVersion,
    DateOnly? Date,
    int? AgeDays,
    string Level,
    string Verdict,
    string? OfficialPage,
    VendorApp? App);

public static class GpuDrivers
{
    /// <summary>Quando vale atualizar o driver de vídeo (texto da página).</summary>
    public const string WhenToUpdate =
        "Vale atualizar a placa de vídeo pelo site do fabricante quando: vai jogar um jogo lançado depois da data do driver (os drivers " +
        "trazem perfis e correções para ele), um jogo pede uma versão mínima, ou há travamentos/artefatos que as notas de versão citam. " +
        "Não mexa se está tudo estável e você não joga nada novo: driver novo não aumenta FPS por si só e às vezes traz problemas. " +
        "Em notebook, a integrada costuma usar o driver do fabricante do notebook; troque só se tiver falhas. " +
        "O Windows Update entrega drivers de vídeo testados para compatibilidade, mas costuma vir meses atrás do site do fabricante.";

    /// <summary>Caminhos fixos dos apps oficiais (Program Files). Nada vem de texto do usuário.</summary>
    public static IReadOnlyList<(GpuVendor Vendor, string Name, string RelativePath)> KnownApps { get; } =
    [
        (GpuVendor.Nvidia, "NVIDIA App", @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
        (GpuVendor.Nvidia, "GeForce Experience", @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
        (GpuVendor.Amd, "AMD Software: Adrenalin Edition", @"AMD\CNext\CNext\RadeonSoftware.exe"),
    ];

    public static VendorApp? FindApp(GpuVendor vendor, IReadOnlyList<string> programFilesRoots, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(programFilesRoots);
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach (var (v, name, rel) in KnownApps.Where(a => a.Vendor == vendor))
            foreach (var root in programFilesRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                var path = System.IO.Path.Combine(root, rel);
                if (fileExists(path)) return new VendorApp(name, path);
            }
        return null;
    }

    /// <summary>Monta o estado de cada placa física (ignora o renderizador por software da Microsoft).</summary>
    public static IReadOnlyList<GpuDriverStatus> From(IEnumerable<GpuAdapter> adapters, DateOnly today, Func<GpuVendor, VendorApp?> app)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(app);
        var list = new List<GpuDriverStatus>();
        foreach (var a in adapters.Where(a => !a.IsSoftware && a.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel))
        {
            var decoded = a.DriverInfoDecoded;
            int? age = a.DriverDate is { } d ? DriverInfo.AgeDays(d, today) : null;
            var (level, verdict) = age is { } days ? DriverInfo.AgeVerdict(days, a.IsIntegrated) : ("info", "Data do driver não informada pelo Windows.");
            list.Add(new GpuDriverStatus(a.Name, a.Vendor, a.IsIntegrated, decoded.WindowsVersion, decoded.VendorVersion, a.DriverDate, age, level, verdict,
                DriverInfo.OfficialDownloadPage(a.Vendor), app(a.Vendor)));
        }
        return list;
    }

    /// <summary>A oferta do Windows Update é da classe de vídeo (vai para o grupo "Placa de vídeo").</summary>
    public static bool IsDisplay(WuDriverUpdate u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return string.Equals(u.DriverClass, "Display", StringComparison.OrdinalIgnoreCase) ||
               u.Title.Contains(" - Display - ", StringComparison.OrdinalIgnoreCase);
    }
}
