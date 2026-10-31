using System.Globalization;
using System.Runtime.Versioning;
using Nexus.DriverUpdates.Windows;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates;

public enum OfferGroup { Recommended, Optional, Graphics }

/// <summary>Uma oferta do Windows Update cruzada com o dispositivo e o driver que ele usa agora.</summary>
public sealed record DriverUpdateOffer(
    WuDriverUpdate Update,
    OfferGroup Group,
    string Device,
    string? InstalledVersion,
    DateOnly? InstalledDate,
    string? InstalledProvider,
    int DeviceCount,
    RollbackPlan Rollback,
    string? Blocked)
{
    /// <summary>Comparação da versão oferecida com a instalada (texto curto para a lista).</summary>
    public string VersionLine
    {
        get
        {
            var offered = Update.OfferedVersion ?? "versão não informada";
            if (InstalledVersion is null) return $"oferecida {offered} · instalada: nenhum driver";
            var cmp = DeviceMatching.CompareVersions(Update.OfferedVersion, InstalledVersion);
            var hint = cmp switch { > 0 => " (mais nova)", < 0 => " (MAIS ANTIGA que a instalada)", 0 => " (igual)", _ => "" };
            return $"oferecida {offered}{hint} · instalada {InstalledVersion}";
        }
    }
}

public sealed record DriverUpdatesReading(
    IReadOnlyList<DriverUpdateOffer> Offers,
    WuErrorKind Error,
    string? ErrorMessage,
    bool? RebootPending,
    IReadOnlyList<GpuDriverStatus> Gpus,
    string? GpuNote,
    string? InventoryNote);

public static class DriverUpdatesReader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Cruza ofertas e inventário (puro; testável).</summary>
    public static IReadOnlyList<DriverUpdateOffer> Combine(IReadOnlyList<WuDriverUpdate> updates, IReadOnlyList<DeviceDriver> devices)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(devices);
        var list = new List<DriverUpdateOffer>();
        foreach (var u in updates.Where(u => u.IsDriver))
        {
            var matched = DeviceMatching.Match(u, devices);
            var primary = matched.FirstOrDefault(d => d.InfName is not null) ?? (matched.Count > 0 ? matched[0] : null);
            var group = GpuDrivers.IsDisplay(u) ? OfferGroup.Graphics : u.Optional ? OfferGroup.Optional : OfferGroup.Recommended;
            string? blocked = null;
            if (!u.EulaAccepted) blocked = "Pede aceitar termos de licença: instale por Configurações > Windows Update.";
            else if (u.OfferedVersion is null) blocked = "Versão oferecida desconhecida: instale por Configurações > Windows Update.";
            else if (primary is null) blocked = "Nenhum dispositivo presente corresponde (aparelho desconectado?).";
            var device = primary?.Description is { Length: > 0 } desc ? desc : u.Model ?? u.Title;
            list.Add(new DriverUpdateOffer(u, group, device, primary?.DriverVersion, primary?.DriverDate, primary?.DriverProvider, matched.Count,
                RollbackRules.Plan(primary), blocked));
        }
        return list.OrderBy(o => o.Group).ThenBy(o => o.Device, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Texto da confirmação: lista tudo, avisa do ponto de restauração, do reinício e do que não tem volta.</summary>
    public static string ConfirmationText(IReadOnlyList<DriverUpdateOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        var lines = offers.Take(15).Select(o => $"• {o.Device}: {o.VersionLine}{(o.Update.Optional ? " (opcional)" : "")}");
        var noRollback = offers.Where(o => !o.Rollback.Possible).ToList();
        var text = (offers.Count == 1 ? "Instalar esta atualização de driver do Windows Update?" : $"Instalar {offers.Count} atualizações de driver do Windows Update?") +
                   "\n\n" + string.Join("\n", lines) + (offers.Count > 15 ? $"\n• … e mais {offers.Count - 15}" : "") +
                   "\n\nComo: o próprio Windows Update baixa e instala (fonte oficial da Microsoft). Antes, o NEXUS cria um ponto de restauração " +
                   "e guarda uma cópia do driver atual de cada dispositivo (pnputil /export-driver).\n" +
                   "Pode pedir reinício: alguns drivers só valem depois de reiniciar. A tela pode piscar ao trocar o driver de vídeo.\n" +
                   "Ganho: correções e compatibilidade; em geral não muda o desempenho.\n" +
                   "Desfazer: reinstala o driver anterior guardado.";
        if (noRollback.Count > 0)
            text += "\n\nSEM DESFAZER AUTOMÁTICO para: " + string.Join(", ", noRollback.Take(5).Select(o => o.Device)) +
                    " — " + noRollback[0].Rollback.Explanation + " Nesses casos, só a Restauração do Sistema volta atrás.";
        return text + "\n\nExige administrador: o Windows vai pedir confirmação.";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "tamanho n/d",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture)} MB",
        _ => $"{(bytes / 1024d / 1024d / 1024d).ToString("0.00", CultureInfo.CurrentCulture)} GB",
    };

    /// <summary>Lê tudo no PC real: busca no Windows Update (com tempo limite e cancelamento), inventário e placas de vídeo.</summary>
    [SupportedOSPlatform("windows")]
    public static DriverUpdatesReading Scan(TimeSpan timeout, CancellationToken cancel, bool searchUpdates = true)
    {
        var agent = new WuaAgent();
        IReadOnlyList<DeviceDriver> devices = [];
        string? inventoryNote = null;
        try { devices = new SetupApiDeviceInventory().List(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { inventoryNote = "Inventário de dispositivos indisponível: " + ex.Message; }

        var (gpus, gpuNote) = ReadGpus();
        bool? reboot = null;
        try { reboot = WindowsRebootState.Pending(agent); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { reboot = null; }

        if (!searchUpdates) return new([], WuErrorKind.None, null, reboot, gpus, gpuNote, inventoryNote);
        var outcome = agent.SearchDrivers(timeout, cancel);
        var offers = outcome.Ok ? Combine(outcome.Updates, devices) : [];
        return new(offers, outcome.Error, outcome.Message, reboot, gpus, gpuNote, inventoryNote);
    }

    [SupportedOSPlatform("windows")]
    private static (IReadOnlyList<GpuDriverStatus>, string?) ReadGpus()
    {
        try
        {
            var report = Nexus.Lab.Gpu.Windows.GpuLabScanner.Scan(null, "não usado", includeGames: false);
            string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
            var gpus = GpuDrivers.From(report.Adapters, DateOnly.FromDateTime(DateTime.Today), v => GpuDrivers.FindApp(v, roots, File.Exists));
            return (gpus, gpus.Count == 0 ? "Nenhuma placa NVIDIA, AMD ou Intel encontrada." : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ([], "Leitura da placa de vídeo indisponível: " + ex.Message); }
    }
}
