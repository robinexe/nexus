using System.Globalization;
using System.Text.Json;

namespace Nexus.Security;

public sealed record SecurityProduct(string Name, ProductState State);

public sealed record FirewallProfileState(string Name, bool Enabled);

public sealed record VolumeEncryption(string Drive, int? Protection, string Source);

public sealed record TpmInfo(bool Present, bool? Enabled, string? Version);

/// <summary>Dados lidos por WMI/CIM (Defender, Central de Segurança, firewall, BitLocker, TPM, tarefas). Campo null = não lido.</summary>
public sealed record WmiSecurityData(
    bool? AmServiceEnabled,
    bool? AntivirusEnabled,
    bool? RealTimeProtection,
    bool? BehaviorMonitor,
    bool? TamperProtected,
    string? RunningMode,
    string? ProductVersion,
    string? SignatureVersion,
    int? SignatureAgeDays,
    DateTimeOffset? SignatureUpdated,
    DateTimeOffset? QuickScanEnd,
    int? QuickScanAgeDays,
    DateTimeOffset? FullScanEnd,
    int? FullScanAgeDays,
    int? MapsReporting,
    int? ControlledFolderAccess,
    int? PuaProtection,
    DefenderExclusions? Exclusions,
    IReadOnlyList<SecurityProduct> Antivirus,
    IReadOnlyList<SecurityProduct> Firewalls,
    IReadOnlyList<FirewallProfileState> FirewallProfiles,
    IReadOnlyList<ThreatRecord> Threats,
    IReadOnlyList<VolumeEncryption> BitLocker,
    TpmInfo? Tpm,
    IReadOnlyList<ScheduledTaskInfo> Tasks,
    IReadOnlyList<string> Errors);

/// <summary>Dados lidos direto do registro (UAC, SmartScreen, Secure Boot, firewall, reinício pendente).</summary>
public sealed record RegistrySecurityFlags(
    bool? UacEnabled,
    int? ConsentPromptAdmin,
    string? SmartScreenExplorer,
    bool? SmartScreenPolicyOff,
    bool? SecureBoot,
    bool RebootPending,
    IReadOnlyList<FirewallProfileState> FirewallFromRegistry);

/// <summary>Parser puro do JSON produzido pelo script de leitura (<see cref="WindowsSecurityReader"/>) e o placar.</summary>
public static class SecurityStatus
{
    public static WmiSecurityData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var errors = new List<string>();
        foreach (var name in new[] { "statusError", "prefError", "avError", "threatError" })
            if (Str(r, name) is { } e) errors.Add(e);

        var s = Obj(r, "status");
        var p = Obj(r, "pref");
        DefenderExclusions? exclusions = null;
        if (p is { } pref)
        {
            var paths = StrArray(pref, "ExclusionPath");
            var exts = StrArray(pref, "ExclusionExtension");
            var procs = StrArray(pref, "ExclusionProcess");
            // Sem administrador, o Defender responde "N/A: Must be an administrator to view exclusions".
            var hidden = paths.Concat(exts).Concat(procs).Any(x => x.StartsWith("N/A", StringComparison.OrdinalIgnoreCase));
            exclusions = new DefenderExclusions(hidden ? [] : paths, hidden ? [] : exts, hidden ? [] : procs, hidden);
        }

        var threatsById = new Dictionary<string, (string Name, int Severity, bool Active)>(StringComparer.Ordinal);
        foreach (var t in Arr(r, "threats"))
            if (Scalar(t, "ThreatID") is { } id)
                threatsById[id] = (Str(t, "ThreatName") ?? "Ameaça desconhecida", Int(t, "SeverityID") ?? 0, Bool(t, "IsActive") ?? false);
        var threats = new List<ThreatRecord>();
        foreach (var d in Arr(r, "detections"))
        {
            var id = Scalar(d, "ThreatID") ?? "";
            var info = threatsById.TryGetValue(id, out var x) ? x : ("Ameaça " + id, 0, false);
            var status = Int(d, "ThreatStatusID") ?? 0;
            threats.Add(new ThreatRecord(info.Item1, SeverityName(info.Item2), Date(d, "InitialDetectionTime"), StatusName(status), info.Item3,
                StrArray(d, "Resources").Take(5).ToList(), Recommendation(status, info.Item3)));
        }
        if (threats.Count == 0)
            foreach (var (id, t) in threatsById)
                threats.Add(new ThreatRecord(t.Name, SeverityName(t.Severity), null, t.Active ? "Ativa" : "Registrada", t.Active, [], Recommendation(t.Active ? 1 : 0, t.Active)));

        return new WmiSecurityData(
            s is { } a ? Bool(a, "AMServiceEnabled") : null,
            s is { } b ? Bool(b, "AntivirusEnabled") : null,
            s is { } c ? Bool(c, "RealTimeProtectionEnabled") : null,
            s is { } d2 ? Bool(d2, "BehaviorMonitorEnabled") : null,
            s is { } e2 ? Bool(e2, "IsTamperProtected") : null,
            s is { } f ? Str(f, "AMRunningMode") : null,
            s is { } g ? Str(g, "AMProductVersion") : null,
            s is { } h ? Str(h, "AntivirusSignatureVersion") : null,
            s is { } i ? Int(i, "AntivirusSignatureAge") : null,
            s is { } j ? Date(j, "AntivirusSignatureLastUpdated") : null,
            s is { } k ? Date(k, "QuickScanEndTime") : null,
            s is { } l ? Int(l, "QuickScanAge") : null,
            s is { } m ? Date(m, "FullScanEndTime") : null,
            s is { } n ? Int(n, "FullScanAge") : null,
            p is { } p1 ? Int(p1, "MAPSReporting") : null,
            p is { } p2 ? Int(p2, "EnableControlledFolderAccess") : null,
            p is { } p3 ? Int(p3, "PUAProtection") : null,
            exclusions,
            Products(r, "av"),
            Products(r, "fw"),
            Arr(r, "fwProfiles").Select(x => new FirewallProfileState(Str(x, "Name") ?? "?", Bool(x, "Enabled") ?? false)).ToList(),
            threats,
            Arr(r, "bitlocker").Select(x => new VolumeEncryption(Str(x, "Drive") ?? "?", Int(x, "Protection"), Str(x, "Source") ?? "?")).ToList(),
            Obj(r, "tpm") is { } tpm ? new TpmInfo(Bool(tpm, "Present") ?? false, Bool(tpm, "Enabled"), Str(tpm, "Version")) : null,
            Arr(r, "tasks").Select(x => new ScheduledTaskInfo(Str(x, "Path") ?? "?", Str(x, "Execute") ?? "", Str(x, "Arguments"),
                !string.Equals(Str(x, "State"), "Disabled", StringComparison.OrdinalIgnoreCase), Str(x, "Author"))).ToList(),
            errors);
    }

    private static List<SecurityProduct> Products(JsonElement r, string name) =>
        Arr(r, name).Select(x => (Name: Str(x, "displayName"), State: Int(x, "productState")))
            .Where(x => x.Name is not null && x.State is not null)
            .Select(x => new SecurityProduct(x.Name!, ProductState.Decode(unchecked((uint)x.State!.Value)))).ToList();

    public static string SeverityName(int id) => id switch { 1 => "Baixa", 2 => "Moderada", 4 => "Alta", 5 => "Grave", _ => "Desconhecida" };

    /// <summary>ThreatStatusID de MSFT_MpThreatDetection.</summary>
    public static string StatusName(int id) => id switch
    {
        1 => "Detectada",
        2 => "Limpa",
        3 => "Em quarentena",
        4 => "Removida",
        5 => "Permitida",
        6 => "Bloqueada",
        102 => "Falha ao pôr em quarentena",
        103 => "Falha ao remover",
        104 => "Falha ao permitir",
        105 => "Abandonada",
        107 => "Falha ao bloquear",
        _ => "Desconhecido",
    };

    public static string Recommendation(int statusId, bool active) => statusId switch
    {
        2 or 3 or 4 or 6 when !active => "Resolvido pelo Defender. Nada a fazer. Se for falso positivo, restaure pela Segurança do Windows > Histórico de proteção.",
        5 => "Você permitiu este item. Confirme que é confiável; se não for, remova a permissão na Segurança do Windows.",
        _ => "Ação pendente: abra a Segurança do Windows e escolha Remover ou Quarentena. Depois rode uma verificação completa (ou a Verificação Offline).",
    };

    // ------------------------------------------------------------------ placar

    public const string UriThreat = "windowsdefender://threat";
    public const string UriThreatSettings = "windowsdefender://threatsettings";
    public const string UriFirewall = "windowsdefender://network";
    public const string UriAppBrowser = "windowsdefender://appbrowser";
    public const string UriDevice = "windowsdefender://devicesecurity";
    public const string UriUpdate = "ms-settings:windowsupdate";

    public static IReadOnlyList<ProtectionItem> Scoreboard(WmiSecurityData? w, RegistrySecurityFlags f, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(f);
        var items = new List<ProtectionItem>();
        var thirdParty = w?.Antivirus.Where(a => !a.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase) && a.State.Scanner == ScannerState.On).ToList() ?? [];
        var defenderOn = w?.AntivirusEnabled == true && w.AmServiceEnabled == true && !string.Equals(w.RunningMode, "Passive Mode", StringComparison.OrdinalIgnoreCase);

        // Antivírus
        if (w is null) items.Add(new("Antivírus", Tone.Unknown, "Não lido", "A leitura do Defender e da Central de Segurança falhou.", "Abra a Segurança do Windows para conferir.", UriThreat));
        else if (defenderOn) items.Add(new("Antivírus", Tone.Good, "Microsoft Defender ativo", $"Versão {w.ProductVersion ?? "?"}" + (thirdParty.Count > 0 ? $"; também registrado: {string.Join(", ", thirdParty.Select(t => t.Name))}" : ""), null, UriThreat));
        else if (thirdParty.Count > 0) items.Add(new("Antivírus", Tone.Good, $"{thirdParty[0].Name} ativo", $"Estado na Central de Segurança: {thirdParty[0].State.Describe()} (formato não documentado pela Microsoft). O Defender fica em modo passivo.", null, UriThreat));
        else items.Add(new("Antivírus", Tone.Bad, "Nenhum antivírus ativo", "Nem o Defender nem outro antivírus está ligado.", "Ligue o Microsoft Defender na Segurança do Windows.", UriThreatSettings));

        // Tempo real
        if (w?.RealTimeProtection is { } rt && (defenderOn || thirdParty.Count == 0))
            items.Add(rt ? new("Proteção em tempo real", Tone.Good, "Ligada", "Arquivos são verificados ao abrir e baixar.", null, UriThreatSettings)
                         : new("Proteção em tempo real", Tone.Bad, "Desligada", "Arquivos novos não são verificados.", "Ligue em Segurança do Windows > Gerenciar configurações. O NEXUS não muda isso: a Proteção contra Adulteração bloqueia (e deve bloquear) mudanças por programas.", UriThreatSettings));
        else if (thirdParty.Count > 0)
            items.Add(new("Proteção em tempo real", thirdParty[0].State.Scanner == ScannerState.On ? Tone.Good : Tone.Warn, "A cargo de " + thirdParty[0].Name, thirdParty[0].State.Describe(), null, UriThreat));

        if (defenderOn)
        {
            // Nuvem (MAPS)
            if (w!.MapsReporting is { } maps)
                items.Add(maps == 0 ? new("Proteção na nuvem", Tone.Warn, "Desligada", "Sem a nuvem, ameaças novas demoram mais para ser reconhecidas.", "Ligue em Gerenciar configurações > Proteção fornecida na nuvem.", UriThreatSettings)
                                    : new("Proteção na nuvem", Tone.Good, "Ligada", maps == 2 ? "Associação avançada." : "Associação básica.", null, UriThreatSettings));

            // Assinaturas
            if (w.SignatureAgeDays is { } age)
            {
                var tone = age <= 1 ? Tone.Good : age <= 7 ? Tone.Warn : Tone.Bad;
                var when = w.SignatureUpdated is { } u ? $"atualizadas em {u.ToLocalTime():dd/MM HH:mm}" : $"{age} dia(s)";
                items.Add(new("Assinaturas de vírus", tone, age == 0 ? "Em dia" : $"{age} dia(s) de idade", $"Versão {w.SignatureVersion ?? "?"}, {when}.",
                    tone == Tone.Good ? null : "Use \"Atualizar assinaturas\" aqui ou o Windows Update.", UriThreat));
            }

            // Verificações
            var quick = w.QuickScanEnd is { } q && q.Year > 2000 ? q : (DateTimeOffset?)null;
            var full = w.FullScanEnd is { } fu && fu.Year > 2000 ? fu : (DateTimeOffset?)null;
            var qAge = quick is { } qq ? (int)(now - qq).TotalDays : (int?)null;
            items.Add(new("Última verificação", qAge is null ? Tone.Warn : qAge <= 7 ? Tone.Good : qAge <= 30 ? Tone.Warn : Tone.Bad,
                quick is { } qd ? $"Rápida: {qd.ToLocalTime():dd/MM/yyyy}" : "Nenhuma verificação rápida registrada",
                full is { } fd ? $"Completa: {fd.ToLocalTime():dd/MM/yyyy}." : "Nenhuma verificação completa registrada (o Defender faz rápidas sozinho; a completa é manual).",
                qAge is null or > 7 ? "Rode uma verificação rápida." : null, UriThreat));

            // Adulteração
            if (w.TamperProtected is { } tp)
                items.Add(tp ? new("Proteção contra adulteração", Tone.Good, "Ligada", "Programas (inclusive malware e o próprio NEXUS) não conseguem desligar o Defender.", null, UriThreatSettings)
                             : new("Proteção contra adulteração", Tone.Warn, "Desligada", "Malware pode desligar o Defender sem você ver.", "Ligue em Gerenciar configurações > Proteção contra adulteração.", UriThreatSettings));

            if (w.PuaProtection is { } pua)
                items.Add(pua == 1 ? new("Bloqueio de apps indesejados (PUA)", Tone.Good, "Ligado", "Bloqueia instaladores com adware e mineradores.", null, UriAppBrowser)
                                   : new("Bloqueio de apps indesejados (PUA)", Tone.Warn, pua == 2 ? "Só auditoria" : "Desligado", "Instaladores com adware passam sem aviso.", "Ligue em Controle de aplicativos e do navegador > Proteção baseada em reputação.", UriAppBrowser));

            if (w.ControlledFolderAccess is { } cfa)
                items.Add(new("Acesso controlado a pastas", cfa == 1 ? Tone.Good : Tone.Unknown, cfa == 1 ? "Ligado" : cfa == 2 ? "Só auditoria" : "Desligado (opcional)",
                    "Protege Documentos e Imagens contra ransomware. É opcional: pode bloquear jogos que salvam em Documentos até você liberá-los.", null, "windowsdefender://ransomwareprotection"));
        }

        // Firewall
        var profiles = w?.FirewallProfiles.Count > 0 ? w.FirewallProfiles : f.FirewallFromRegistry;
        var thirdFw = w?.Firewalls.Where(x => x.State.Scanner == ScannerState.On && !x.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        if (thirdFw.Count > 0)
            items.Add(new("Firewall", Tone.Good, $"{thirdFw[0].Name} ativo", thirdFw[0].State.Describe(), null, UriFirewall));
        else if (profiles.Count > 0)
        {
            var off = profiles.Where(x => !x.Enabled).Select(x => ProfileName(x.Name)).ToList();
            items.Add(off.Count == 0
                ? new("Firewall", Tone.Good, "Ligado em todos os perfis", string.Join(", ", profiles.Select(x => ProfileName(x.Name))), null, UriFirewall)
                : new("Firewall", off.Any(o => o == "Público") ? Tone.Bad : Tone.Warn, $"Desligado: {string.Join(", ", off)}", "Com o firewall desligado, serviços do PC ficam expostos na rede.", "Ligue em Segurança do Windows > Firewall e proteção de rede.", UriFirewall));
        }
        else items.Add(new("Firewall", Tone.Unknown, "Não lido", "", null, UriFirewall));

        // SmartScreen
        if (f.SmartScreenPolicyOff == true || string.Equals(f.SmartScreenExplorer, "Off", StringComparison.OrdinalIgnoreCase))
            items.Add(new("SmartScreen", Tone.Warn, "Desligado", f.SmartScreenPolicyOff == true ? "Desligado por política." : "Arquivos baixados não são conferidos pela reputação.", "Ligue em Controle de aplicativos e do navegador.", UriAppBrowser));
        else
            items.Add(new("SmartScreen", Tone.Good, f.SmartScreenExplorer is null ? "Ligado (padrão)" : "Ligado", "Avisa antes de abrir programas baixados desconhecidos.", null, UriAppBrowser));

        // UAC
        if (f.UacEnabled == false) items.Add(new("Controle de Conta de Usuário", Tone.Bad, "Desligado", "Qualquer programa roda como administrador sem perguntar.", "Ative o UAC em Painel de Controle > Contas de Usuário > Alterar configurações de Controle de Conta de Usuário (exige reiniciar).", null));
        else if (f.UacEnabled == true && f.ConsentPromptAdmin == 0) items.Add(new("Controle de Conta de Usuário", Tone.Warn, "Nunca notificar", "Programas sobem para administrador sem aviso.", "Suba o controle deslizante do UAC para o padrão.", null));
        else if (f.UacEnabled == true) items.Add(new("Controle de Conta de Usuário", Tone.Good, "Ligado", "Pede confirmação antes de mudanças de administrador.", null, null));
        else items.Add(new("Controle de Conta de Usuário", Tone.Unknown, "Não lido", "", null, null));

        // Secure Boot / TPM
        items.Add(f.SecureBoot switch
        {
            true => new("Secure Boot", Tone.Good, "Ligado", "Só sistemas de inicialização assinados carregam.", null, UriDevice),
            false => new("Secure Boot", Tone.Warn, "Desligado", "Bootkits podem carregar antes do Windows. Alguns anticheats (ex.: Vanguard, FACEIT) exigem Secure Boot.", "Ligue na configuração da UEFI (BIOS). O NEXUS não altera firmware.", UriDevice),
            _ => new("Secure Boot", Tone.Unknown, "Não suportado ou não lido", "PCs com BIOS antiga (legado) não têm Secure Boot.", null, UriDevice),
        });
        if (w?.Tpm is { } tpmInfo)
            items.Add(tpmInfo.Present
                ? new("TPM", tpmInfo.Enabled == false ? Tone.Warn : Tone.Good, $"Presente{(tpmInfo.Version is null ? "" : " (" + tpmInfo.Version + ")")}", tpmInfo.Enabled == false ? "Presente mas desativado." : "Chip de segurança usado por BitLocker, Windows Hello e alguns anticheats.", null, UriDevice)
                : new("TPM", Tone.Warn, "Não encontrado", "Sem TPM visível (pode estar desligado na UEFI: fTPM/PTT).", null, UriDevice));

        // BitLocker
        if (w?.BitLocker.Count > 0)
        {
            var parts = w.BitLocker.Select(b => $"{b.Drive} {BitLockerName(b.Protection)}").ToList();
            var system = w.BitLocker.FirstOrDefault(b => b.Drive.StartsWith(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase)) ?? w.BitLocker[0];
            var on = system.Protection is 1 or 6;
            items.Add(new("BitLocker", on ? Tone.Good : Tone.Unknown, on ? "Disco do sistema criptografado" : "Disco do sistema sem criptografia",
                string.Join("; ", parts) + ". Protege seus dados se o PC ou o disco for roubado; não protege contra vírus.",
                on ? "Guarde a chave de recuperação (conta Microsoft ou impressa)." : "Opcional. No Windows Home, veja Configurações > Privacidade e segurança > Criptografia do dispositivo.", null));
        }

        // Windows Update
        items.Add(f.RebootPending
            ? new("Windows Update", Tone.Warn, "Reinício pendente", "Há atualização instalada esperando reiniciar para valer.", "Reinicie quando puder.", UriUpdate)
            : new("Windows Update", Tone.Good, "Nenhum reinício pendente", "O NEXUS só lê se há reinício pendente; para ver atualizações disponíveis, abra o Windows Update.", null, UriUpdate));
        return items;
    }

    public static string ProfileName(string n) => n.ToLowerInvariant() switch
    {
        "domain" or "domainprofile" => "Domínio",
        "private" or "standardprofile" => "Privado",
        "public" or "publicprofile" => "Público",
        _ => n,
    };

    /// <summary>System.Volume.BitLockerProtection (Shell); o script converte ProtectionStatus do WMI para a mesma escala (1 ligado, 2 desligado).</summary>
    public static string BitLockerName(int? p) => p switch
    {
        1 => "ligado",
        2 => "desligado",
        3 => "criptografando",
        4 => "descriptografando",
        5 => "suspenso",
        6 => "ligado (bloqueado)",
        8 => "aguardando ativação",
        _ => "sem informação",
    };

    // ------------------------------------------------------------------ JSON tolerante (ConvertTo-Json devolve objeto OU lista)

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

    private static List<JsonElement> Arr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return [];
        return p.ValueKind switch
        {
            JsonValueKind.Array => p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object => [p],
            _ => [],
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? Scalar(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch { JsonValueKind.String => p.GetString(), JsonValueKind.Number => p.GetRawText(), _ => null };
    }

    private static int? Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l)) return l is > int.MaxValue or < int.MinValue ? unchecked((int)l) : (int)l;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (p.ValueKind == JsonValueKind.True) return 1;
        if (p.ValueKind == JsonValueKind.False) return 0;
        return null;
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => p.TryGetInt32(out var i) ? i != 0 : null,
            _ => null,
        };
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static List<string> StrArray(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return [];
        return p.ValueKind switch
        {
            JsonValueKind.Array => p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
            JsonValueKind.String => [p.GetString()!],
            _ => [],
        };
    }
}
