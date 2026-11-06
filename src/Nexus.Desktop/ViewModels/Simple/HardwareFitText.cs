using Nexus.Assistant;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// Recomendação automática pelo hardware (lógica pura, testada em HardwareRecommendationTests): o motivo de cada veredito em
// palavras simples (chave resx), o que fica marcado por padrão nos Ajustes rápidos e a frase do perfil em Ajustes › Perfil do PC.
// As regras em si estão em Nexus.Assistant (HardwareRules); aqui só texto e seleção.
// =====================================================================================================================

/// <summary>Textos dos vereditos por hardware ("recomendado para o seu PC", "não indicado para notebook").</summary>
public static class FitText
{
    /// <summary>O motivo em uma frase curta (chave resx "Fit.*", sem valor técnico); vazio para "sem motivo".</summary>
    public static string Reason(FitReason r) => r switch
    {
        FitReason.LaptopHeat => Loc.T("Fit.LaptopHeat"),
        FitReason.OnBattery => Loc.T("Fit.OnBattery"),
        FitReason.IntegratedGpu => Loc.T("Fit.IntegratedGpu"),
        FitReason.WeakGpuRelief => Loc.T("Fit.WeakGpuRelief"),
        FitReason.StrongGpuNoNeed => Loc.T("Fit.StrongGpuNoNeed"),
        FitReason.StrongGpuUse => Loc.T("Fit.StrongGpuUse"),
        FitReason.LowRamRelief => Loc.T("Fit.LowRamRelief"),
        FitReason.SysMainHelpsHere => Loc.T("Fit.SysMainHelpsHere"),
        FitReason.HddFewerWrites => Loc.T("Fit.HddFewerWrites"),
        FitReason.NotNvidia => Loc.T("Fit.NotNvidia"),
        FitReason.NotAmd => Loc.T("Fit.NotAmd"),
        FitReason.NotIntel => Loc.T("Fit.NotIntel"),
        FitReason.NvidiaGpu => Loc.T("Fit.NvidiaGpu"),
        FitReason.AmdGpu => Loc.T("Fit.AmdGpu"),
        FitReason.IntelGpu => Loc.T("Fit.IntelGpu"),
        FitReason.Competitive => Loc.T("Fit.Competitive"),
        FitReason.LaptopWifi => Loc.T("Fit.LaptopWifi"),
        FitReason.LaptopCooler => Loc.T("Fit.LaptopCooler"),
        FitReason.Uncapped => Loc.T("Fit.Uncapped"),
        FitReason.X3dParking => Loc.T("Fit.X3dParking"),
        FitReason.DesktopOnly => Loc.T("Fit.DesktopOnly"),
        FitReason.BackgroundCapture => Loc.T("Fit.BackgroundCapture"),
        _ => "",
    };

    /// <summary>O motivo em uma frase ("não indicado para notebook: esquenta e faz barulho"), ou vazio para neutro.</summary>
    public static string Sentence(Fit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return fit.Verdict == FitVerdict.Neutral ? "" : Reason(fit.Reason);
    }
}

/// <summary>Seleção padrão dos Ajustes rápidos e do Automático a partir do veredito por hardware.</summary>
public static class QuickTweakSelection
{
    /// <summary>
    /// Marcado por padrão = seria marcado pelas regras gerais (risco baixo, um clique) E o hardware não desaconselha. Um ajuste só
    /// "recomendado pelo hardware" não vira padrão sozinho: o risco baixo continua obrigatório.
    /// </summary>
    public static bool IsRecommended(bool lowRiskOneClick, Fit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return lowRiskOneClick && !fit.IsNotRecommended;
    }
}

/// <summary>Ajustes › "Perfil do PC": o perfil detectado em palavras simples ("Notebook com placa de entrada (GTX 1650, 4 GB), 16 GB de RAM, SSD").</summary>
public static class HardwareProfileText
{
    public static string Headline(HardwareProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var form = p.IsLaptop switch { true => Loc.T("Profile.Laptop"), false => Loc.T("Profile.Desktop"), _ => Loc.T("Profile.Pc") };
        var gpu = p.GpuClass switch
        {
            GpuClass.Integrated => Loc.T("Profile.GpuIntegrated"),
            GpuClass.Entry => Loc.T("Profile.GpuEntry"),
            GpuClass.Mid => Loc.T("Profile.GpuMid"),
            GpuClass.Strong => Loc.T("Profile.GpuStrong"),
            _ => "",
        };
        var detail = p.GpuName is null ? "" : p.VramGb is { } v && p.GpuClass != GpuClass.Integrated ? $" ({p.GpuName}, {Loc.F("Profile.Gb", v)})" : $" ({p.GpuName})";
        var parts = new List<string> { gpu.Length == 0 ? form : Loc.F("Profile.FormWithGpu", form, gpu + detail) };
        if (p.MemoryGb is { } ram) parts.Add(Loc.F("Profile.Ram", ram));
        if (p.Ssd is { } ssd) parts.Add(ssd ? Loc.T("Profile.Ssd") : Loc.T("Profile.Hdd"));
        if (p.OnBattery == true) parts.Add(Loc.T("Profile.OnBattery"));
        return string.Join(", ", parts);
    }

    /// <summary>Lista do que ficou sem leitura ("tipo do disco não lido"), ou vazio.</summary>
    public static string Unknowns(HardwareProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var list = new List<string>();
        if (p.GpuClass == GpuClass.Unknown) list.Add(Loc.T("Profile.Unknown.Gpu"));
        if (p.MemoryGb is null) list.Add(Loc.T("Profile.Unknown.Ram"));
        if (p.IsLaptop is null) list.Add(Loc.T("Profile.Unknown.Form"));
        if (p.Ssd is null) list.Add(Loc.T("Profile.Unknown.Disk"));
        return list.Count == 0 ? "" : Loc.F("Profile.Unknown.List", string.Join(", ", list));
    }
}
