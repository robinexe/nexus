using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Desktop.ViewModels;

namespace Nexus.Desktop.Views.Optimize;

/// <summary>Uma fonte de evidência com o nível em texto e a força (1 a 5) para o medidor.</summary>
public sealed record EvidenceItem(string LevelText, string Source, int Strength)
{
    /// <summary>Largura do trecho preenchido no medidor de 40 px (8 px por nível).</summary>
    public double MeterWidth => Math.Clamp(Strength, 0, 5) * 8.0;
}

/// <summary>Mito ou ajuste recusado pelo catálogo, já com título e veredito legíveis.</summary>
public sealed record MythItem(string Id, string Title, string Verdict, string Tone, string Reason, string? Source)
{
    public bool HasSource => !string.IsNullOrWhiteSpace(Source);
}

/// <summary>Grupo de resultados da simulação (seria aplicada, já está assim, não se aplica, bloqueada).</summary>
public sealed record PlanGroup(PlanStatus Kind, string Title, string Hint, IReadOnlyList<PlanRow> Items)
{
    public int Count => Items.Count;
}

/// <summary>Capacidades de uma área, com a contagem honesta do que já foi validado.</summary>
public sealed record CapabilityGroup(string Area, IReadOnlyList<CapabilityRow> Items)
{
    public int Validated => Items.Count(i => i.Label == CapabilityLabel.ImplementedValidated);
    public string Tally => Validated == Items.Count
        ? (Items.Count == 1 ? "validado" : $"todos os {Items.Count} validados")
        : $"{Validated} de {Items.Count} validados";
}

/// <summary>Textos de apoio do catálogo em português de jogador.</summary>
public static class CatalogText
{
    public static string Classification(Classification c) => c switch
    {
        Nexus.Core.Classification.SupportedDocumented => "Documentado",
        Nexus.Core.Classification.Conditional => "Depende do seu caso",
        Nexus.Core.Classification.Experimental => "Experimental",
        Nexus.Core.Classification.Obsolete => "Obsoleto",
        Nexus.Core.Classification.Ineffective => "Sem efeito",
        Nexus.Core.Classification.Incompatible => "Incompatível",
        _ => "Recusado",
    };

    public static string Reboot(RebootRequirement r) => r switch
    {
        RebootRequirement.None => "Vale na hora",
        RebootRequirement.GameRestart => "Reabra o jogo",
        RebootRequirement.NextLogon => "No próximo logon",
        RebootRequirement.Recommended => "Reinício recomendado",
        _ => "Exige reinício",
    };

    public static string Persistence(PersistenceKind p) =>
        p == PersistenceKind.Session ? "Só nesta sessão" : "Fica até você reverter";

    public static string Privileges(string raw)
    {
        var admin = raw.Contains("Admin", StringComparison.Ordinal);
        var user = raw.Contains("User", StringComparison.Ordinal);
        return admin && user ? "Admin em alguns casos" : admin ? "Precisa de admin" : "Sem admin";
    }

    public static (string Text, int Strength) Evidence(EvidenceLevel level) => level switch
    {
        EvidenceLevel.VendorDocumented => ("Documentado pelo fabricante", 5),
        EvidenceLevel.Mechanistic => ("Mecanismo conhecido", 4),
        EvidenceLevel.Observed => ("Observado em teste", 3),
        EvidenceLevel.Community => ("Relato da comunidade", 2),
        _ => ("Hipótese", 1),
    };

    private static readonly Dictionary<string, string> MythTitles = new(StringComparer.Ordinal)
    {
        ["myth.timer-resolution-service"] = "Serviço de timer resolution",
        ["myth.mmcss-sfio-priority"] = "Prioridade SFIO do MMCSS",
        ["myth.mmcss-nolazymode"] = "NoLazyMode do MMCSS",
        ["myth.bcd-hpet-dynamictick"] = "Mexer em HPET e dynamic tick no BCD",
        ["myth.standby-list-purge-periodic"] = "Limpar a memória em espera toda hora",
        ["myth.disable-pagefile"] = "Desligar o arquivo de paginação",
        ["myth.tcpnodelay-tcpackfrequency"] = "TCPNoDelay e TcpAckFrequency para o ping",
        ["myth.dns-for-ping"] = "Trocar o DNS para baixar o ping",
        ["myth.realtime-priority"] = "Prioridade tempo real no jogo",
        ["myth.disable-smt-cores-universal"] = "Desligar SMT/Hyper-Threading em qualquer PC",
        ["myth.registry-cleaner"] = "Limpador de registro",
        ["myth.clear-shader-cache"] = "Apagar o cache de shaders",
        ["myth.winutil-presumed-original"] = "Voltar para um \"padrão\" fixo de script",
        ["myth.disable-security-features"] = "Desligar VBS, HVCI ou Defender",
    };

    public static string MythTitle(string id)
    {
        if (MythTitles.TryGetValue(id, out var title)) return title;
        var slug = id.StartsWith("myth.", StringComparison.Ordinal) ? id[5..] : id;
        slug = slug.Replace('-', ' ').Replace('.', ' ');
        return slug.Length == 0 ? id : char.ToUpperInvariant(slug[0]) + slug[1..];
    }

    /// <summary>Veredito curto e tom ("bad" quando não é recomendado; "neutral" quando só não ajuda).</summary>
    public static (string Verdict, string Tone, int Order) MythVerdict(string classification) => classification switch
    {
        "Rejected" => ("Não recomendado", "bad", 0),
        "Incompatible" => ("Incompatível", "bad", 1),
        "Ineffective" => ("Não faz efeito", "neutral", 2),
        "Obsolete" => ("Ficou no passado", "neutral", 3),
        "Experimental" => ("Sem prova de ganho", "neutral", 4),
        _ => (classification, "neutral", 5),
    };
}
