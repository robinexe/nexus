using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.DriverUpdates;

/// <summary>
/// Uma atualização como o Windows Update Agent a entrega (IUpdate + IWindowsDriverUpdate). Interface sobre o COM: a
/// implementação real lê as propriedades por IDispatch; os testes usam um registro falso.
/// </summary>
public interface IWuUpdateRecord
{
    string Title { get; }
    string UpdateId { get; }
    int Revision { get; }
    /// <summary>UpdateType do WUA: 1 = software, 2 = driver.</summary>
    int Type { get; }
    bool IsInstalled { get; }
    bool IsHidden { get; }
    /// <summary>Verdadeiro nas "atualizações opcionais" (Configurações > Windows Update > Opções avançadas > Atualizações opcionais).</summary>
    bool BrowseOnly { get; }
    bool EulaAccepted { get; }
    bool IsDownloaded { get; }
    decimal MaxDownloadSize { get; }
    IReadOnlyList<string> KbArticleIds { get; }
    string? SupportUrl { get; }
    IReadOnlyList<string> MoreInfoUrls { get; }
    DateTime? LastDeploymentChangeTime { get; }
    string? DriverClass { get; }
    string? DriverHardwareId { get; }
    string? DriverManufacturer { get; }
    string? DriverModel { get; }
    string? DriverProvider { get; }
    DateTime? DriverVerDate { get; }
}

/// <summary>Atualização de driver já traduzida para o NEXUS (sem COM).</summary>
public sealed record WuDriverUpdate(
    string UpdateId,
    int Revision,
    string Title,
    bool IsDriver,
    bool Optional,
    bool EulaAccepted,
    bool IsDownloaded,
    long SizeBytes,
    string? DriverClass,
    string? HardwareId,
    string? Manufacturer,
    string? Model,
    string? Provider,
    DateOnly? DriverDate,
    string? OfferedVersion,
    IReadOnlyList<string> KbArticles,
    string? DetailsUrl)
{
    /// <summary>Valor do argumento "updateId" do catálogo (GUID minúsculo).</summary>
    public string Key => $"{UpdateId}|{Revision.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Por que a busca no Windows Update não trouxe resultado.</summary>
public enum WuErrorKind { None, Offline, ServiceDisabled, Policy, Busy, Timeout, Canceled, Unsupported, Other }

public sealed record WuSearchOutcome(IReadOnlyList<WuDriverUpdate> Updates, WuErrorKind Error, string? Message, int? HResult = null)
{
    public bool Ok => Error == WuErrorKind.None;
    public static WuSearchOutcome Success(IReadOnlyList<WuDriverUpdate> updates) => new(updates, WuErrorKind.None, null);
    public static WuSearchOutcome Fail(WuErrorKind kind, string message, int? hresult = null) => new([], kind, message, hresult);
}

/// <summary>Resultado de baixar e instalar uma atualização pelo WUA.</summary>
/// <param name="ResultCode">OperationResultCode: 2 sucesso, 3 sucesso com erros, 4 falha, 5 abortado.</param>
public sealed record WuInstallResult(int ResultCode, bool RebootRequired, int HResult, string? Message)
{
    public bool Succeeded => ResultCode is 2 or 3;
}

/// <summary>O Windows Update Agent (Microsoft.Update.Session). Busca em leitura; baixar/instalar só no aplicador elevado.</summary>
public interface IWindowsUpdateAgent
{
    /// <summary>Busca <see cref="WindowsUpdateQueries.DriverSearch"/>. Nunca lança: falhas viram <see cref="WuSearchOutcome.Error"/>.</summary>
    WuSearchOutcome SearchDrivers(TimeSpan timeout, CancellationToken cancel);

    /// <summary>Baixa e instala UMA atualização (UpdateID + revisão) já conferida pelo chamador.</summary>
    WuInstallResult DownloadAndInstall(string updateId, int revision, TimeSpan timeout);

    /// <summary>O Windows Update diz que há reinício pendente (Microsoft.Update.SystemInfo.RebootRequired). Null se não deu para ler.</summary>
    bool? RebootRequired();
}

/// <summary>Regras puras sobre os dados do Windows Update (testáveis sem COM).</summary>
public static partial class WindowsUpdateQueries
{
    /// <summary>Critério oficial: drivers não instalados e não ocultos (inclui as "atualizações opcionais de driver").</summary>
    public const string DriverSearch = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    public const int UpdateTypeDriver = 2;

    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateIdPattern();

    [GeneratedRegex(@"(?<v>\d{1,5}(?:\.\d{1,6}){1,3})\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingVersion();

    /// <summary>Mesmas regras do catálogo (userParameters): GUID minúsculo e revisão de 1 a 6 dígitos.</summary>
    public const string UpdateIdRegex = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
    public const string RevisionRegex = "[0-9]{1,6}";

    public static bool IsValidUpdateId(string? id) => id is not null && UpdateIdPattern().IsMatch(id);

    public static bool TryParseRevision(string? text, out int revision)
    {
        revision = 0;
        return text is { Length: >= 1 and <= 6 } && text.All(char.IsAsciiDigit) &&
               int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    /// <summary>
    /// Versão oferecida: o WUA não tem campo de versão para drivers; o título segue "Fabricante - Classe - a.b.c.d"
    /// (ex.: "NVIDIA - Display - 32.0.15.6094"). Null quando o título não termina numa versão.
    /// </summary>
    public static string? VersionFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var m = TrailingVersion().Match(title.Trim());
        if (!m.Success) return null;
        var v = m.Groups["v"].Value;
        return v.Contains('.', StringComparison.Ordinal) && Version.TryParse(v, out _) ? v : null;
    }

    public static WuDriverUpdate Map(IWuUpdateRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var details = FirstAllowedUrl([.. r.MoreInfoUrls, .. (r.SupportUrl is null ? Array.Empty<string>() : [r.SupportUrl])]);
        var kbs = r.KbArticleIds.Where(k => k.Length is > 0 and <= 10 && k.All(char.IsAsciiDigit)).Select(k => "KB" + k).ToList();
        return new WuDriverUpdate(
            r.UpdateId.ToLowerInvariant(), r.Revision, r.Title.Trim(), r.Type == UpdateTypeDriver, r.BrowseOnly, r.EulaAccepted, r.IsDownloaded,
            r.MaxDownloadSize is > 0 and < long.MaxValue ? (long)r.MaxDownloadSize : 0,
            Clean(r.DriverClass), Clean(r.DriverHardwareId), Clean(r.DriverManufacturer), Clean(r.DriverModel), Clean(r.DriverProvider),
            r.DriverVerDate is { } d && d.Year > 1990 ? DateOnly.FromDateTime(d) : null,
            VersionFromTitle(r.Title), kbs, details);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Hosts de onde o NEXUS aceita abrir "mais informações" (só domínios da Microsoft, só https).</summary>
    public static readonly IReadOnlyList<string> AllowedDetailHosts =
        ["microsoft.com", "windowsupdate.com", "catalog.update.microsoft.com"];

    public static bool IsAllowedDetailsUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
        var host = u.IdnHost.ToLowerInvariant();
        return AllowedDetailHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    private static string? FirstAllowedUrl(IEnumerable<string> urls) => urls.Select(u => u.Trim()).FirstOrDefault(IsAllowedDetailsUrl);

    /// <summary>Link do Catálogo do Microsoft Update para o UpdateID (página oficial com os detalhes do pacote).</summary>
    public static string CatalogUrl(string updateId) =>
        IsValidUpdateId(updateId) ? "https://catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=" + updateId : throw new ArgumentException("UpdateID inválido.", nameof(updateId));

    /// <summary>Tradução honesta dos erros mais comuns do Windows Update (códigos documentados pela Microsoft).</summary>
    public static (WuErrorKind Kind, string Message) DescribeError(int hresult)
    {
        var code = unchecked((uint)hresult);
        return code switch
        {
            0x80072EE2 or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x80072F8F or 0x8024402C or 0x8024401C or 0x80244022 or 0x8024402F or 0x80244017 =>
                (WuErrorKind.Offline, "Sem conexão com o Windows Update (internet fora do ar, proxy ou DNS). Confira a conexão e tente de novo."),
            0x80070422 or 0x8024001E or 0x8024002B =>
                (WuErrorKind.ServiceDisabled, "O serviço Windows Update está desativado ou parando. Sem ele não há como procurar drivers oficiais; o NEXUS não liga o serviço sozinho."),
            0x8024002E or 0x80240025 or 0x8024500C or 0x80244018 or 0x8024A000 =>
                (WuErrorKind.Policy, "Uma política da empresa (ou do administrador) bloqueia o acesso ao Windows Update ou às atualizações de driver neste PC. Fale com o responsável pela TI."),
            0x80240016 or 0x8024A005 or 0x80240044 =>
                (WuErrorKind.Busy, "O Windows Update está ocupado com outra instalação. Espere terminar (ou reinicie) e procure de novo."),
            0x80070005 =>
                (WuErrorKind.Policy, "Acesso negado pelo Windows Update (permissão ou política)."),
            _ => (WuErrorKind.Other, $"O Windows Update respondeu com o erro 0x{code:X8}."),
        };
    }
}
