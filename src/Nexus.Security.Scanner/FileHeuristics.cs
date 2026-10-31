using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Security.Scanner;

/// <summary>Onde e quando o arquivo está (para as heurísticas de local/idade).</summary>
public sealed record FileContext(string Path, long Size, DateTime CreatedUtc, PathZone Zone, DateTime NowUtc);

/// <summary>Heurísticas pelo NOME do arquivo (valem para qualquer tipo): RTLO, extensão dupla, preenchimento com espaços.</summary>
public static partial class NameHeuristics
{
    /// <summary>Extensões que o Windows executa ao abrir (duplo clique).</summary>
    public static readonly IReadOnlySet<string> ExecutableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".com", ".pif", ".cpl", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".ps1", ".msi", ".jar",
    };

    [GeneratedRegex(@"\.(pdf|docx?|xlsx?|pptx?|txt|rtf|odt|jpe?g|png|gif|bmp|mp3|mp4|avi|mkv|zip|rar|7z)\s*\.(exe|scr|com|pif|cpl|bat|cmd|vbs|vbe|js|jse|wsf|hta|lnk|ps1|msi|jar)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoubleExtension();

    [GeneratedRegex(@"\s{4,}\.(exe|scr|com|pif|cpl|bat|cmd|vbs|vbe|js|jse|wsf|hta|lnk|ps1|msi|jar)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpacePadding();

    /// <summary>Caracteres de controle de direção que invertem como o nome aparece (RTLO "fatura‮fdp.exe" vira "faturaexe.pdf").</summary>
    private static readonly System.Buffers.SearchValues<char> BidiControls = System.Buffers.SearchValues.Create("\u202E\u202D\u202B\u202A\u2066\u2067\u2068\u2069\u200F\u200E");

    public static IReadOnlyList<ScanHit> Analyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var hits = new List<ScanHit>();
        if (fileName.AsSpan().IndexOfAny(BidiControls) >= 0)
            hits.Add(new ScanHit(ScanLayer.Heuristic, "name.rtlo", "Nome com caractere que inverte o texto (RTLO)",
                "O nome tem um caractere invisível de controle de direção. Ele faz \"fatura‮fdp.exe\" aparecer como \"faturaexe.pdf\": a extensão real fica escondida. Não há uso legítimo comum para isso em nomes de arquivo (MITRE ATT&CK T1036.002).", 60));
        if (DoubleExtension().IsMatch(fileName))
            hits.Add(new ScanHit(ScanLayer.Heuristic, "name.double-extension", "Extensão dupla (parece documento, mas é programa)",
                $"O nome termina em \"{TailOf(fileName)}\": com as extensões ocultas (padrão do Windows) ele aparece como documento/imagem, mas abrir executa um programa ou script (MITRE ATT&CK T1036.007).", 45));
        else if (SpacePadding().IsMatch(fileName))
            hits.Add(new ScanHit(ScanLayer.Heuristic, "name.space-padding", "Muitos espaços antes da extensão",
                "Vários espaços antes de \".exe\" (ou outra extensão executável) empurram a extensão real para fora da coluna do Explorer. É um truque para esconder que é um programa (MITRE ATT&CK T1036.006).", 40));
        return hits;
    }

    private static string TailOf(string name)
    {
        var last = name.LastIndexOf('.');
        var prev = last > 0 ? name.LastIndexOf('.', last - 1) : -1;
        return prev >= 0 ? name[prev..] : name;
    }
}

/// <summary>
/// Heurística de executáveis (PE): cada sinal tem pontos e explicação. Nenhum sinal sozinho prova nada; a soma vira
/// "para revisar"/"suspeito"/"muito suspeito" pelos limiares de <see cref="Scoring"/>. Assinatura digital válida limita
/// os pontos (programas legítimos assinados também usam compressão, overlay etc.).
/// </summary>
public static class PeHeuristics
{
    /// <summary>Processos do Windows que malware costuma imitar (nome sem extensão).</summary>
    public static readonly IReadOnlyList<string> SystemProcessNames =
    [
        "svchost", "lsass", "csrss", "winlogon", "services", "smss", "wininit", "spoolsv", "taskhostw", "dllhost", "rundll32", "conhost",
        "explorer", "ctfmon", "runtimebroker", "searchindexer", "taskmgr", "msmpeng", "sihost", "fontdrvhost", "dwm", "audiodg",
        "wuauclt", "regsvr32", "mshta", "lsm", "userinit", "taskhost", "searchhost", "securityhealthservice",
    ];

    private static readonly string[] PackerSections = ["UPX0", "UPX1", "UPX2", ".aspack", ".adata", ".MPRESS1", ".MPRESS2", ".themida", ".vmp0", ".vmp1", ".vmp2", ".petite", ".nsp0", ".nsp1", "PEC2", ".packed", ".enigma1", ".enigma2"];

    private static readonly DateTime Year1992 = new(1992, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Nome do arquivo num caminho do Windows (aceita \ e /, em qualquer SO).</summary>
    public static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    public static bool IsRiskZone(PathZone z) => z is PathZone.Temp or PathZone.Downloads or PathZone.UserAppData or PathZone.Public;

    /// <summary>Sinais que não dependem da assinatura (rápidos). A assinatura entra depois em <see cref="ApplySignature"/>.</summary>
    public static List<ScanHit> Analyze(PeFile pe, FileContext ctx)
    {
        ArgumentNullException.ThrowIfNull(pe);
        ArgumentNullException.ThrowIfNull(ctx);
        var hits = new List<ScanHit>();

        // Seções que podem ser escritas E executadas: código que se modifica/desempacota na memória.
        var wx = pe.Sections.Where(s => s.IsExecutable && s.IsWritable).Select(s => s.Name).ToList();
        if (wx.Count > 0)
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.wx-section", "Seção de código que também pode ser escrita",
                $"A seção {Names(wx)} é executável e gravável ao mesmo tempo. Compiladores normais separam código (só leitura) de dados; W+X é típico de empacotadores e de código que se descompacta ou se altera na memória.", 20));

        // Entropia alta em seção executável: código comprimido/criptografado (empacotado).
        var packed = pe.Sections.Where(s => s.IsExecutable && s.RawSize >= 4096 && !double.IsNaN(s.Entropy) && s.Entropy >= 7.2).ToList();
        if (packed.Count > 0)
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.high-entropy", "Código comprimido ou criptografado (empacotado)",
                $"A seção {Names(packed.Select(s => s.Name))} tem entropia {packed.Max(s => s.Entropy).ToString("0.00", CultureInfo.InvariantCulture)} de 8 (quase aleatória). Código de verdade fica perto de 6; acima de 7,2 indica compressão ou criptografia, que esconde o conteúdo de antivírus. Alguns programas legítimos também usam compressores.", 20));

        var packerNames = pe.Sections.Select(s => s.Name).Where(n => PackerSections.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (packerNames.Count > 0)
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.packer-section", "Seções de empacotador conhecido",
                $"Seções {Names(packerNames)} são criadas por empacotadores (UPX, MPRESS, Themida, VMProtect...). Eles são usados por programas legítimos (para reduzir tamanho ou proteger contra cópia) e também por malware, para dificultar a análise.", 10));

        // Imports combinados.
        var imports = new HashSet<string>(pe.ImportedFunctions.Select(StripAw), StringComparer.OrdinalIgnoreCase);
        bool Has(params string[] names) => names.Any(imports.Contains);
        if (Has("VirtualAllocEx", "NtAllocateVirtualMemory") && Has("WriteProcessMemory", "NtWriteVirtualMemory") &&
            Has("CreateRemoteThread", "NtCreateThreadEx", "RtlCreateUserThread", "QueueUserAPC", "SetThreadContext"))
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.imports-injection", "Funções para injetar código em outro programa",
                "Importa, juntas, as funções para reservar memória em outro processo, escrever nela e iniciar uma linha de execução lá (VirtualAllocEx + WriteProcessMemory + CreateRemoteThread ou equivalentes). É a receita de injeção de processo (MITRE ATT&CK T1055). Depuradores e algumas ferramentas de sistema também fazem isso.", 30));
        if (Has("SetWindowsHookEx") && Has("GetAsyncKeyState", "GetKeyState", "GetKeyboardState", "CallNextHookEx") &&
            Has("GetForegroundWindow", "GetWindowText", "GetKeyNameText", "MapVirtualKey", "ToUnicode", "ToAscii"))
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.imports-keylogger", "Funções típicas de captura de teclado",
                "Combina gancho global de teclado (SetWindowsHookEx) com leitura do estado das teclas e da janela em primeiro plano. É o conjunto usado por registradores de teclas (MITRE ATT&CK T1056.001). Programas de atalhos/macros legítimos também usam ganchos.", 25));
        var total = pe.ImportedFunctions.Count();
        if (!pe.IsDotNet && total is > 0 and <= 6 && Has("LoadLibrary", "LdrLoadDll") && Has("GetProcAddress", "LdrGetProcedureAddress"))
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.imports-minimal", "Importa quase nada (resolve funções escondido)",
                $"Só {total} funções importadas, entre elas LoadLibrary e GetProcAddress: o programa descobre o resto em tempo de execução. É típico de empacotadores e de quem quer esconder o que o programa usa.", 15));
        else if (!pe.IsDotNet && total == 0 && pe.Subsystem is 2 or 3)
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.imports-none", "Executável sem tabela de importação",
                "Um programa do Windows sem nenhuma função importada precisa montar tudo sozinho na memória. Isso quase só acontece com código empacotado ou montado à mão.", 15));

        // Carimbo de data impossível (exceto build reproduzível, em que o campo é um hash).
        if (!pe.HasReproDebugEntry && pe.TimeDateStamp != 0)
        {
            var when = DateTime.UnixEpoch.AddSeconds(pe.TimeDateStamp);
            if (when > ctx.NowUtc.AddDays(2) || when < Year1992)
                hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.timestamp", "Data de compilação impossível",
                    $"O cabeçalho diz que foi compilado em {when:dd/MM/yyyy}, {(when > ctx.NowUtc ? "no futuro" : "antes do formato existir")}. Pode ser data adulterada para confundir análise, mas alguns compiladores gravam um número qualquer: sozinho, não significa nada.", 5));
        }

        // Nome imitando processo do Windows fora da pasta do sistema.
        var fileName = FileNameOf(ctx.Path);
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (ctx.Zone != PathZone.System)
        {
            if (SystemProcessNames.Contains(name) && ctx.Zone != PathZone.ProgramFiles)
                hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.system-name", "Nome de processo do Windows fora da pasta do Windows",
                    $"\"{fileName}\" é o nome de um processo do Windows, que só existe em C:\\Windows (System32). Uma cópia em outra pasta é disfarce comum: no Gerenciador de Tarefas ela parece parte do sistema (MITRE ATT&CK T1036.005).", 35));
            else if (LookalikeOf(name) is { } original)
                hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.lookalike-name", "Nome parecido com processo do Windows",
                    $"\"{fileName}\" difere de \"{original}.exe\" por uma letra trocada ou um caractere parecido (0 por o, 1 por l...). É o disfarce de \"erro de digitação\" para passar despercebido na lista de processos (MITRE ATT&CK T1036.005).", 40));
        }

        // Executável recém-criado em pasta de risco.
        if (IsRiskZone(ctx.Zone) && ctx.NowUtc - ctx.CreatedUtc < TimeSpan.FromDays(7) && ctx.CreatedUtc <= ctx.NowUtc.AddMinutes(5))
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.fresh-risky-location", "Executável novo em pasta de risco",
                $"Criado em {ctx.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} em {ZoneText(ctx.Zone)}. Programas instalados ficam em Arquivos de Programas; executáveis novos em Temp/AppData/Downloads costumam ser instaladores baixados agora (normal) ou algo que outro programa deixou ali (suspeito).", 10));

        // Overlay grande (dados depois das seções, sem contar a assinatura).
        if (pe.OverlaySize > 1024 * 1024 && pe.FileLength > 0 && pe.OverlaySize * 100 / pe.FileLength >= 30)
            hits.Add(new ScanHit(ScanLayer.Heuristic, "pe.large-overlay", "Muitos dados anexados ao fim do executável",
                $"{pe.OverlaySize / (1024 * 1024)} MB ({pe.OverlaySize * 100 / pe.FileLength}% do arquivo) estão depois das seções. Instaladores legítimos guardam ali o que vão instalar; droppers guardam o malware. Normal em instaladores assinados.", 10));

        return hits;
    }

    /// <summary>
    /// Inclui o sinal da assinatura e aplica o teto: assinatura válida → pontos da heurística de PE limitados a
    /// <see cref="Scoring.SignedHeuristicCap"/>; sem assinatura em pasta de risco → +15; sem assinatura em outro lugar → +5.
    /// </summary>
    public static List<ScanHit> ApplySignature(List<ScanHit> peHits, bool? validSignature, FileContext ctx)
    {
        ArgumentNullException.ThrowIfNull(peHits);
        ArgumentNullException.ThrowIfNull(ctx);
        if (validSignature == true)
        {
            var budget = Scoring.SignedHeuristicCap;
            var capped = new List<ScanHit>();
            foreach (var h in peHits.OrderByDescending(h => h.Points))
            {
                var p = Math.Min(h.Points, budget);
                budget -= p;
                capped.Add(h with { Points = p });
            }
            capped.Add(new ScanHit(ScanLayer.Heuristic, "pe.signed", "Assinatura digital válida",
                "O Windows confirmou a assinatura Authenticode (o arquivo não foi alterado depois de assinado e o certificado é aceito). Isso não garante que o programa é bom, mas reduz muito a chance; por isso os demais sinais pesam pouco.", 0));
            return capped;
        }
        if (validSignature == false)
        {
            var risky = IsRiskZone(ctx.Zone);
            peHits.Add(new ScanHit(ScanLayer.Heuristic, "pe.unsigned", "Sem assinatura digital",
                risky ? $"Executável sem assinatura válida em {ZoneText(ctx.Zone)}. Programas de empresas conhecidas costumam ser assinados; sem assinatura não dá para saber quem fez nem se foi alterado."
                      : "Executável sem assinatura válida. Muitos programas pequenos e jogos antigos não são assinados; só pesa junto com outros sinais.",
                risky ? 15 : 5));
        }
        return peHits;
    }

    /// <summary>Processo do Windows de que <paramref name="name"/> é imitação (troca de 1 letra, transposição ou homóglifo). null = nenhum.</summary>
    public static string? LookalikeOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length < 5 || SystemProcessNames.Contains(name)) return null;
        var normalized = Homoglyphs(name);
        foreach (var sys in SystemProcessNames)
        {
            if (sys.Length < 5) continue;
            if (Homoglyphs(sys) == normalized) return sys;
            if (sys.Length == name.Length && SubstitutionOrTransposition(sys, name)) return sys;
        }
        return null;
    }

    private static string Homoglyphs(string s) => s.ToLowerInvariant()
        .Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal)
        .Replace('0', 'o').Replace('1', 'l').Replace('i', 'l').Replace('3', 'e').Replace('5', 's').Replace('@', 'a').Replace('$', 's');

    /// <summary>Mesma extensão de texto e: exatamente uma letra diferente, ou duas vizinhas trocadas.</summary>
    private static bool SubstitutionOrTransposition(string a, string b)
    {
        var diff = new List<int>(3);
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) { diff.Add(i); if (diff.Count > 2) return false; }
        if (diff.Count == 1) return char.IsLetter(b[diff[0]]) || char.IsDigit(b[diff[0]]);
        return diff.Count == 2 && diff[1] == diff[0] + 1 && a[diff[0]] == b[diff[1]] && a[diff[1]] == b[diff[0]];
    }

    private static string StripAw(string fn) =>
        fn.Length > 1 && (fn[^1] == 'A' || fn[^1] == 'W') && char.IsLower(fn[^2]) ? fn[..^1] : fn.EndsWith("ExA", StringComparison.Ordinal) || fn.EndsWith("ExW", StringComparison.Ordinal) ? fn[..^1] : fn;

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Distinct().Select(n => $"\"{n}\""));

    public static string ZoneText(PathZone z) => z switch
    {
        PathZone.Temp => "uma pasta temporária (Temp)",
        PathZone.Downloads => "Downloads",
        PathZone.UserAppData => "AppData (dados de programas do usuário)",
        PathZone.Public => "a pasta Pública",
        PathZone.ProgramFiles => "Arquivos de Programas",
        PathZone.System => "a pasta do Windows",
        _ => "uma pasta comum",
    };
}
