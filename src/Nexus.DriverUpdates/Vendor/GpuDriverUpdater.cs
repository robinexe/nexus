using System.Globalization;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates.Vendor;

public enum GpuUpdateState
{
    /// <summary>Já tem a versão mais nova do fabricante.</summary>
    UpToDate,
    /// <summary>Há versão mais nova, com instalador oficial conferido (host e tamanho).</summary>
    Available,
    /// <summary>AMD sem ramo identificado: só o "Auto-Detect and Install" oficial (aberto para o usuário ver).</summary>
    AutoDetectOnly,
    /// <summary>O fabricante não publica driver novo para esta placa (ou o NEXUS não sabe instalar: Intel).</summary>
    Unavailable,
    /// <summary>Sem internet, site fora do ar, resposta estranha. Nada foi baixado.</summary>
    Failed,
}

/// <summary>Resultado de "Procurar versão nova" para uma placa.</summary>
public sealed record GpuUpdateCheck(
    GpuUpdateState State,
    string Message,
    VendorDriverOffer? Offer = null,
    long ExactBytes = 0,
    Uri? AutoDetectUrl = null,
    string? Note = null);

/// <summary>Instalador baixado e conferido na interface (o aplicador confere tudo de novo antes de executar).</summary>
public sealed record DownloadedInstaller(GpuVendor Vendor, string Version, string Path, string Sha256, string Signer, long Bytes);

/// <summary>
/// Orquestra "procurar versão nova" e "baixar" para a página (leitura de rede + gravação só na pasta de downloads do usuário).
/// A instalação é sempre pelo aplicador (ação <c>driverupdates.gpu.install</c>).
/// </summary>
public sealed class GpuDriverUpdater(VendorDriverClient client, ISignatureVerifier verifier) : IDisposable
{
    private readonly VendorDriverClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ISignatureVerifier _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    private IReadOnlyList<NvidiaProduct>? _products;

    public void Dispose() => _client.Dispose();

    public async Task<GpuUpdateCheck> CheckAsync(GpuDriverStatus gpu, int windowsBuild, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        try
        {
            return gpu.Vendor switch
            {
                GpuVendor.Nvidia => await CheckNvidiaAsync(gpu, windowsBuild, cancel).ConfigureAwait(false),
                GpuVendor.Amd => await CheckAmdAsync(gpu, windowsBuild, cancel).ConfigureAwait(false),
                _ => new GpuUpdateCheck(GpuUpdateState.Unavailable, "Para Intel, o NEXUS ainda não baixa o driver: use o assistente oficial da Intel (link abaixo)."),
            };
        }
        catch (VendorDriverException ex) { return new GpuUpdateCheck(GpuUpdateState.Failed, ex.Message); }
    }

    private async Task<GpuUpdateCheck> CheckNvidiaAsync(GpuDriverStatus gpu, int windowsBuild, CancellationToken cancel)
    {
        if (_products is null)
        {
            try
            {
                var series = await _client.GetTextAsync(NvidiaDriverLookup.SeriesUrl, cancel).ConfigureAwait(false);
                var products = await _client.GetTextAsync(NvidiaDriverLookup.ProductsUrl, cancel).ConfigureAwait(false);
                var parsed = NvidiaDriverLookup.ParseProducts(series, products);
                if (parsed.Count > 0) _products = parsed;
            }
            catch (VendorDriverException) { /* cai para a tabela de Device ID */ }
        }
        var product = NvidiaDriverLookup.Resolve(_products, gpu.Name, gpu.DeviceId, gpu.Laptop);
        if (product is null) return new GpuUpdateCheck(GpuUpdateState.Unavailable, "Não deu para identificar esta placa na lista da NVIDIA. Use a página oficial.");
        var json = await _client.GetTextAsync(NvidiaDriverLookup.LookupUrl(product, NvidiaDriverLookup.OsId(windowsBuild)), cancel).ConfigureAwait(false);
        var (offer, problem) = NvidiaDriverLookup.ParseLookup(json, product.Notebook);
        if (offer is null) return new GpuUpdateCheck(GpuUpdateState.Unavailable, problem ?? "Sem driver novo.");
        var note = product.Notebook
            ? "Notebook: o driver Game Ready da NVIDIA para notebooks é suportado em notebooks com Optimus (placa NVIDIA + integrada); o da Acer/fabricante não é obrigatório."
            : null;
        return await Finish(gpu, offer, note, null, cancel).ConfigureAwait(false);
    }

    private async Task<GpuUpdateCheck> CheckAmdAsync(GpuDriverStatus gpu, int windowsBuild, CancellationToken cancel)
    {
        var branch = AmdDriverLookup.Classify(gpu.DeviceId);
        if (branch == AmdDriverBranch.Legacy)
            return new GpuUpdateCheck(GpuUpdateState.Unavailable, "A AMD não publica mais drivers novos para esta geração (Radeon HD 7000 / R7 / R9). O driver atual é o último.");
        var notebookNote = gpu.Integrated || gpu.Laptop
            ? "Notebook: a AMD recomenda o driver do fabricante do notebook (ex.: Acer), que pode ser personalizado. O Adrenalin da AMD suporta os Ryzen móveis; se algo falhar depois, use Desfazer."
            : null;
        if (AmdDriverLookup.BranchPage(branch) is not { } page)
        {
            var general = await _client.GetTextAsync(AmdDriverLookup.DriversPage, cancel).ConfigureAwait(false);
            var tool = AmdDriverLookup.ParseAutoDetectUrl(general);
            return tool is null
                ? new GpuUpdateCheck(GpuUpdateState.Unavailable, "Não deu para identificar esta placa AMD nem achar o assistente oficial. Use a página oficial.")
                : new GpuUpdateCheck(GpuUpdateState.AutoDetectOnly, "Placa AMD não identificada pelo NEXUS: o assistente oficial da AMD (Auto-Detect and Install) escolhe o driver certo.",
                    AutoDetectUrl: tool, Note: notebookNote);
        }
        var html = await _client.GetTextAsync(page, cancel).ConfigureAwait(false);
        var (offer, problem) = AmdDriverLookup.ParseProductPage(html, windowsBuild >= 22000);
        var autoDetect = AmdDriverLookup.ParseAutoDetectUrl(html);
        if (offer is null)
            return autoDetect is not null
                ? new GpuUpdateCheck(GpuUpdateState.AutoDetectOnly, (problem ?? "") + " O assistente oficial da AMD escolhe o driver certo.", AutoDetectUrl: autoDetect, Note: notebookNote)
                : new GpuUpdateCheck(GpuUpdateState.Unavailable, problem ?? "Sem driver novo.");
        var note = notebookNote;
        if (gpu.PackageVersion is null)
            note = (note is null ? "" : note + " ") + "O Windows não informa a versão do Adrenalin instalada; o NEXUS compara depois de instalar.";
        return await Finish(gpu, offer, note, autoDetect, cancel).ConfigureAwait(false);
    }

    private async Task<GpuUpdateCheck> Finish(GpuDriverStatus gpu, VendorDriverOffer offer, string? note, Uri? autoDetect, CancellationToken cancel)
    {
        var installed = gpu.ComparableVersion;
        if (VendorVersions.Compare(gpu.Vendor, installed, offer.Version) >= 0)
            return new GpuUpdateCheck(GpuUpdateState.UpToDate, $"Em dia: {installed} é a mais nova.", offer, Note: note);
        var exact = await _client.ProbeSizeAsync(gpu.Vendor, offer.DownloadUrl, cancel).ConfigureAwait(false);
        if (!VendorDriverPolicy.SizeMatchesAnnounced(exact, offer.ApproxBytes))
            return new GpuUpdateCheck(GpuUpdateState.Failed, "O tamanho do instalador no servidor não confere com o anunciado pelo fabricante: nada foi baixado.");
        var msg = installed is null
            ? $"Versão mais nova do fabricante: {offer.Version}."
            : $"Há versão nova: {offer.Version} (você tem {installed}).";
        return new GpuUpdateCheck(GpuUpdateState.Available, msg, offer, exact, autoDetect, note);
    }

    // ------------------------------------------------------------------ baixar e conferir

    /// <summary>
    /// Baixa para a pasta de downloads do usuário, confere tamanho, SHA-256 e assinatura (assinante do fabricante). Assinatura
    /// inválida = apaga o arquivo e lança (nunca executa). Espaço insuficiente = lança antes de baixar.
    /// </summary>
    public async Task<DownloadedInstaller> DownloadAsync(GpuUpdateCheck check, string userProfile, IProgress<VendorDownloadProgress>? progress,
        Action? verifying, Func<string, long?> freeSpace, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(freeSpace);
        if (check is not { State: GpuUpdateState.Available, Offer: { } offer } || check.ExactBytes <= 0) throw new VendorDriverException("Nada para baixar.");
        var root = VendorDriverPolicy.UserDownloadRoot(userProfile);
        var path = VendorDriverPolicy.InstallerPath(root, offer.Vendor, offer.Version);
        var already = File.Exists(path + ".partial") ? new FileInfo(path + ".partial").Length : 0;
        if (SpaceProblem(freeSpace(root), check.ExactBytes - already) is { } space) throw new VendorDriverException(space);
        CleanOld(root, offer.Vendor, offer.Version);
        await _client.DownloadAsync(offer.Vendor, offer.DownloadUrl, check.ExactBytes, path, progress, cancel).ConfigureAwait(false);
        verifying?.Invoke();
        var (sha, sig) = await Task.Run(() => (VendorDriverClient.Sha256(path), _verifier.Verify(path)), cancel).ConfigureAwait(false);
        if (!sig.Valid || !VendorDriverPolicy.IsAllowedSigner(offer.Vendor, sig.Signer))
        {
            TryDelete(path);
            throw new VendorDriverException(!sig.Valid
                ? $"Assinatura digital inválida ({sig.Reason}): o arquivo foi apagado e NUNCA foi executado."
                : $"O arquivo está assinado por \"{sig.Signer}\", não pela {GpuIdentity.VendorName(offer.Vendor)}: apagado, NUNCA executado.");
        }
        return new DownloadedInstaller(offer.Vendor, offer.Version, path, sha, sig.Signer!, check.ExactBytes);
    }

    /// <summary>Precisa do que falta baixar + folga para o instalador extrair (~3 GB). Null = cabe.</summary>
    public static string? SpaceProblem(long? free, long remaining)
    {
        if (free is not { } f) return null;
        var need = Math.Max(0, remaining) + VendorDriverPolicy.ExtraFreeSpaceBytes;
        return f >= need ? null
            : $"Espaço insuficiente no disco: precisa de {DriverUpdatesReader.FormatSize(need)} livres (tem {DriverUpdatesReader.FormatSize(f)}). Libere espaço na página Limpeza e tente de novo.";
    }

    /// <summary>Apaga downloads de outras versões do mesmo fabricante (só subpastas com nome de versão válido, sem seguir link).</summary>
    public static void CleanOld(string root, GpuVendor vendor, string keep)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(root, VendorDriverPolicy.VendorToken(vendor)));
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || sub.Name == keep || !VendorDriverPolicy.IsValidVersion(vendor, sub.Name)) continue;
                foreach (var f in sub.EnumerateFiles()) TryDelete(f.FullName);
                try { sub.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Apaga o instalador baixado depois de instalado (≈1 GB).</summary>
    public static void Discard(DownloadedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        TryDelete(installer.Path);
        TryDelete(installer.Path + ".partial");
        try { Directory.Delete(Path.GetDirectoryName(installer.Path)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Baixa o "Auto-Detect and Install" da AMD (~50 MB) para a pasta de downloads e confere a assinatura.</summary>
    public async Task<string> DownloadAutoDetectAsync(Uri url, string userProfile, IProgress<VendorDownloadProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!VendorDriverPolicy.IsAllowedDownload(GpuVendor.Amd, url)) throw new VendorDriverException("Endereço fora do servidor da AMD: recusado.");
        var size = await _client.ProbeSizeAsync(GpuVendor.Amd, url, cancel).ConfigureAwait(false);
        if (size > 200L * 1024 * 1024) throw new VendorDriverException("O assistente da AMD veio grande demais: recusado.");
        var path = Path.Combine(VendorDriverPolicy.UserDownloadRoot(userProfile), "amd", "auto-detect", "amd-auto-detect.exe");
        await _client.DownloadAsync(GpuVendor.Amd, url, size, path, progress, cancel).ConfigureAwait(false);
        var sig = _verifier.Verify(path);
        if (!sig.Valid || !VendorDriverPolicy.IsAllowedSigner(GpuVendor.Amd, sig.Signer))
        {
            TryDelete(path);
            throw new VendorDriverException($"Assinatura da AMD não confere ({sig.Reason}): apagado, NUNCA executado.");
        }
        return path;
    }

    /// <summary>Texto da confirmação (lista o que acontece, risco, reinício e desfazer).</summary>
    public static string ConfirmationText(GpuDriverStatus gpu, DownloadedInstaller installer, string? note)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(installer);
        var vendor = GpuIdentity.VendorName(installer.Vendor);
        var from = gpu.ComparableVersion ?? gpu.WindowsVersion;
        return $"Instalar o driver {vendor} {installer.Version} em \"{gpu.Name}\"?\n\n" +
               $"• Agora: {from}\n• Novo: {installer.Version} ({DriverUpdatesReader.FormatSize(installer.Bytes)}, baixado do servidor oficial da {vendor})\n" +
               $"• Assinatura digital conferida: {installer.Signer}\n• SHA-256: {installer.Sha256[..16]}…\n\n" +
               "Como: o aplicador do NEXUS copia o instalador para uma pasta protegida, confere o SHA-256 e a assinatura de novo, guarda o driver atual " +
               "(pnputil /export-driver), cria um ponto de restauração e roda o instalador do fabricante em modo silencioso.\n" +
               "A tela pode piscar ou ficar preta por alguns segundos. Pode pedir reinício.\n" +
               (note is null ? "" : note + "\n") +
               "Ganho: correções e perfis para jogos novos; não aumenta FPS por si só.\n" +
               "Desfazer: reinstala o driver anterior guardado.\n\nExige administrador: o Windows vai pedir confirmação.";
    }

    /// <summary>"35,2 MB/s · faltam 2 min" para a barra de progresso.</summary>
    public static string ProgressText(VendorDownloadProgress p)
    {
        var pct = (p.Fraction * 100).ToString("0", CultureInfo.CurrentCulture);
        var speed = p.BytesPerSecond > 0 ? $" · {(p.BytesPerSecond / 1024 / 1024).ToString("0.0", CultureInfo.CurrentCulture)} MB/s" : "";
        var left = p.Remaining is { } r ? r.TotalMinutes >= 1 ? $" · faltam {Math.Ceiling(r.TotalMinutes).ToString(CultureInfo.CurrentCulture)} min" : $" · faltam {Math.Ceiling(r.TotalSeconds).ToString(CultureInfo.CurrentCulture)} s" : "";
        return $"Baixando {pct}% ({DriverUpdatesReader.FormatSize(p.Received)} de {DriverUpdatesReader.FormatSize(p.Total)}){speed}{left}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
