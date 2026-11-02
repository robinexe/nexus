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
    /// <summary>Selo: vermelho quando não é recomendado/incompatível; neutro quando só não ajuda.</summary>
    public Controls.Tone ChipTone => Tone == "bad" ? Controls.Tone.Bad : Controls.Tone.Neutral;
}

/// <summary>Grupo de resultados da simulação (seria aplicada, já está assim, não se aplica, bloqueada).</summary>
public sealed record PlanGroup(PlanStatus Kind, string Title, string Hint, IReadOnlyList<PlanRow> Items)
{
    public int Count => Items.Count;
    /// <summary>Âmbar muda, verde já está, vermelho bloqueada, neutro não se aplica.</summary>
    public Controls.Tone Tone => Kind switch
    {
        PlanStatus.Pending => Controls.Tone.Warn,
        PlanStatus.AlreadyConfigured => Controls.Tone.Good,
        PlanStatus.Blocked => Controls.Tone.Bad,
        _ => Controls.Tone.Neutral,
    };
    public string IconKey => Kind switch
    {
        PlanStatus.Pending => "Icon.ArrowRight",
        PlanStatus.AlreadyConfigured => "Icon.Check",
        PlanStatus.Blocked => "Icon.Lock",
        _ => "Icon.Info",
    };
}

/// <summary>Capacidades de uma área, com a contagem honesta do que já foi validado.</summary>
public sealed record CapabilityGroup(string Area, IReadOnlyList<CapabilityRow> Items)
{
    public int Validated => Items.Count(i => i.Label == CapabilityLabel.ImplementedValidated);
    public string Tally => Validated == Items.Count
        ? (Items.Count == 1 ? Loc.T("Pg.Validado") : Loc.F("Auto.TodosNValidados", Items.Count))
        : Loc.F("Auto.NNValidados", Validated, Items.Count);
    /// <summary>Parcela validada da área (0–100), para a barra.</summary>
    public double Percent => Items.Count == 0 ? 0 : 100.0 * Validated / Items.Count;
    public string IconKey => ViewModels.PageText.AreaIcon(Area);
    public IReadOnlyList<CapabilityCard> Cards => Items.Select(i => new CapabilityCard(i)).ToList();
}

/// <summary>Uma capacidade com o selo do design system (tom + texto curto).</summary>
public sealed record CapabilityCard(CapabilityRow Row)
{
    public string Capability => Row.Capability;
    public string Note => Row.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Row.Note);
    public string LabelText => Row.LabelText;
    public Controls.Tone Tone => Row.Label switch
    {
        CapabilityLabel.ImplementedValidated => Controls.Tone.Good,
        CapabilityLabel.ImplementedNotValidated => Controls.Tone.Info,
        CapabilityLabel.Pending => Controls.Tone.Warn,
        _ => Controls.Tone.Neutral,
    };
    public string ChipText => Row.Label switch
    {
        CapabilityLabel.ImplementedValidated => Loc.T("Pg.Validado2"),
        CapabilityLabel.ImplementedNotValidated => Loc.T("Pg.SemTesteRealAinda"),
        CapabilityLabel.NotSupportedHere => Loc.T("Pg.NaoRodaAqui"),
        _ => Loc.T("Pg.Pendente"),
    };
}

/// <summary>Textos de apoio do catálogo em português de jogador.</summary>
public static class CatalogText
{
    public static string Classification(Classification c) => c switch
    {
        Nexus.Core.Classification.SupportedDocumented => Loc.T("Auto.Documentado"),
        Nexus.Core.Classification.Conditional => Loc.T("Auto.DependeSeuCaso"),
        Nexus.Core.Classification.Experimental => Loc.T("Auto.Experimental"),
        Nexus.Core.Classification.Obsolete => Loc.T("Auto.Obsoleto"),
        Nexus.Core.Classification.Ineffective => Loc.T("Auto.SemEfeito"),
        Nexus.Core.Classification.Incompatible => Loc.T("Auto.Incompativel"),
        _ => Loc.T("Auto.Recusado"),
    };

    public static string Reboot(RebootRequirement r) => r switch
    {
        RebootRequirement.None => Loc.T("Auto.ValeHora"),
        RebootRequirement.GameRestart => Loc.T("Auto.ReabraJogo"),
        RebootRequirement.NextLogon => Loc.T("Auto.ProximoLogon"),
        RebootRequirement.Recommended => Loc.T("Auto.ReinicioRecomendado"),
        _ => Loc.T("Auto.ExigeReinicio"),
    };

    public static string Persistence(PersistenceKind p) =>
        p == PersistenceKind.Session ? Loc.T("Auto.SoNestaSessao") : Loc.T("Auto.FicaAteVoceReverter");

    public static string Privileges(string raw)
    {
        var admin = raw.Contains("Admin", StringComparison.Ordinal);
        var user = raw.Contains("User", StringComparison.Ordinal);
        return admin && user ? Loc.T("Auto.AdminAlgunsCasos") : admin ? Loc.T("Auto.PrecisaAdmin") : Loc.T("Auto.SemAdmin");
    }

    public static (string Text, int Strength) Evidence(EvidenceLevel level) => level switch
    {
        EvidenceLevel.VendorDocumented => (Loc.T("Auto.DocumentadoPeloFabricante"), 5),
        EvidenceLevel.Mechanistic => (Loc.T("Auto.MecanismoConhecido"), 4),
        EvidenceLevel.Observed => (Loc.T("Auto.ObservadoTeste"), 3),
        EvidenceLevel.Community => (Loc.T("Auto.RelatoComunidade"), 2),
        _ => (Loc.T("Auto.Hipotese"), 1),
    };

    private static readonly Dictionary<string, string> MythTitles = new(StringComparer.Ordinal)
    {
        ["myth.timer-resolution-service"] = Loc.T("Auto.ServicoTimerResolution"),
        ["myth.mmcss-sfio-priority"] = Loc.T("Auto.PrioridadeSfioMmcss"),
        ["myth.mmcss-nolazymode"] = Loc.T("Auto.NolazymodeMmcss"),
        ["myth.bcd-hpet-dynamictick"] = Loc.T("Auto.MexerHpetDynamicTick"),
        ["myth.standby-list-purge-periodic"] = Loc.T("Auto.LimparMemoriaEsperaToda"),
        ["myth.disable-pagefile"] = Loc.T("Auto.DesligarArquivoPaginacao"),
        ["myth.tcpnodelay-tcpackfrequency"] = Loc.T("Auto.TcpnodelayTcpackfrequencyPing"),
        ["myth.dns-for-ping"] = Loc.T("Auto.TrocarDnsBaixarPing"),
        ["myth.realtime-priority"] = Loc.T("Auto.PrioridadeTempoRealJogo"),
        ["myth.disable-smt-cores-universal"] = Loc.T("Auto.DesligarSmtHyperThreading"),
        ["myth.registry-cleaner"] = Loc.T("Auto.LimpadorRegistro"),
        ["myth.clear-shader-cache"] = Loc.T("Auto.ApagarCacheShaders"),
        ["myth.winutil-presumed-original"] = Loc.T("Auto.VoltarPadraoFixoScript"),
        ["myth.disable-security-features"] = Loc.T("Auto.DesligarVbsHvciOu"),
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
        "Rejected" => (Loc.T("Auto.NaoRecomendado"), "bad", 0),
        "Incompatible" => (Loc.T("Auto.Incompativel"), "bad", 1),
        "Ineffective" => (Loc.T("Auto.NaoFazEfeito"), "neutral", 2),
        "Obsolete" => (Loc.T("Auto.FicouPassado"), "neutral", 3),
        "Experimental" => (Loc.T("Auto.SemProvaGanho"), "neutral", 4),
        _ => (classification, "neutral", 5),
    };
}
