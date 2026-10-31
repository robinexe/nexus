using System.Buffers.Binary;
using System.Text;

namespace Nexus.Security;

/// <summary>Entrada de inicialização (chave Run/RunOnce ou atalho na pasta Inicializar).</summary>
/// <param name="ExpandedPath">Executável com variáveis de ambiente expandidas (null se não foi possível).</param>
/// <param name="HasValidSignature">Assinatura Authenticode válida (null = não verificado/arquivo ausente).</param>
public sealed record AutorunEntry(string Location, string Name, string Command, string? ExpandedPath, bool FileExists, bool? HasValidSignature);

public sealed record ScheduledTaskInfo(string Path, string Execute, string? Arguments, bool Enabled, string? Author);

public sealed record ProxyInfo(bool UserProxyEnabled, string? UserProxyServer, string? AutoConfigUrl, string? WinHttpProxy);

public sealed record ForcedExtension(string Browser, string Scope, string Id, string? UpdateUrl);

public sealed record DefenderExclusions(IReadOnlyList<string> Paths, IReadOnlyList<string> Extensions, IReadOnlyList<string> Processes, bool Hidden);

public sealed record IfeoEntry(string View, string Image, string Debugger, string Selection);

public sealed record AppInitInfo(string View, string Dlls, bool Load, bool? RequireSigned);

public sealed record WinlogonInfo(string? Shell, string? Userinit, string? UserShellOverride, string? UserShellSelection, string SystemDirectory);

/// <summary>Heurísticas puras (sem E/S). Cada achado diz gravidade, motivo, como verificar e o que fazer.</summary>
public static class Heuristics
{
    public const string IfeoFixAction = "security.persistence.machine.remove";
    public const string UserShellFixAction = "security.persistence.user.remove";
    public const string AppInitFixAction = "security.appinit.clear";
    public const string AppInitFix32Action = "security.appinit.clear.wow64";
    public const string ShellFixAction = "security.winlogon.shell.reset";
    public const string HostsFixAction = "security.hosts.disable-hijack";

    // ------------------------------------------------------------------ inicialização

    public static IReadOnlyList<Finding> Autoruns(IEnumerable<AutorunEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<Finding>();
        foreach (var e in entries)
        {
            var zone = CommandHeuristics.Zone(e.ExpandedPath);
            var signals = CommandHeuristics.Analyze(e.Command);
            var where = $"{e.Location} → {e.Name}";
            foreach (var s in signals)
                list.Add(new Finding("Inicialização", s.Severity, $"Comando suspeito ao iniciar: {e.Name}", Text.Clip($"{where}: {e.Command}"),
                    s.Reason + (s.Decoded is null ? "" : $" Texto decodificado: {s.Decoded}"),
                    "Abra Gerenciador de Tarefas > Aplicativos de inicialização (ou o Autoruns da Microsoft Sysinternals) e veja o editor e o comando.",
                    "Se não reconhecer, desative na página Inicialização e rode uma verificação completa do Defender."));

            if (signals.Count > 0 || e.HasValidSignature == true) continue;
            if (zone is PathZone.Temp or PathZone.Downloads or PathZone.Public && e.FileExists)
                list.Add(new Finding("Inicialização", Severity.High, $"Programa sem assinatura iniciando de pasta temporária: {e.Name}", Text.Clip($"{where}: {e.ExpandedPath}"),
                    "Programas legítimos quase nunca iniciam a partir de Temp, Downloads ou Público. É um padrão comum de malware.",
                    "Clique com o botão direito no arquivo > Propriedades > Assinaturas digitais; envie o arquivo ao VirusTotal se tiver dúvida.",
                    "Faça verificação personalizada desse arquivo com o Defender e desative a entrada na página Inicialização."));
            else if (zone is PathZone.UserAppData or PathZone.ProgramDataRoot && e.FileExists && e.HasValidSignature == false)
                list.Add(new Finding("Inicialização", Severity.Medium, $"Programa sem assinatura iniciando de AppData: {e.Name}", Text.Clip($"{where}: {e.ExpandedPath}"),
                    "Muitos apps legítimos ficam em AppData (Discord, Spotify), mas costumam ser assinados. Sem assinatura merece uma olhada.",
                    "Veja Propriedades > Assinaturas digitais e pesquise o nome do arquivo.",
                    "Se não reconhecer, verifique o arquivo com o Defender e desative a entrada."));
            else if (!e.FileExists && e.ExpandedPath is not null && zone is not PathZone.Unknown)
                list.Add(new Finding("Inicialização", Severity.Low, $"Entrada de inicialização aponta para arquivo inexistente: {e.Name}", Text.Clip($"{where}: {e.ExpandedPath}"),
                    "Sobra de programa removido. Não é perigoso nem deixa o PC lento, mas pode ser usada por malware para se recolocar.",
                    "Confira se o programa ainda está instalado.", "Pode desativar na página Inicialização."));
        }
        return list;
    }

    // ------------------------------------------------------------------ tarefas agendadas

    public static IReadOnlyList<Finding> Tasks(IEnumerable<ScheduledTaskInfo> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var list = new List<Finding>();
        foreach (var t in tasks)
        {
            var command = string.IsNullOrWhiteSpace(t.Arguments) ? t.Execute : $"{t.Execute} {t.Arguments}";
            foreach (var s in CommandHeuristics.Analyze(command))
            {
                // Tarefas da própria Microsoft (\Microsoft\...) com -WindowStyle Hidden são comuns: gravidade menor.
                var sev = s.Severity == Severity.Medium && t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ? Severity.Low : s.Severity;
                list.Add(new Finding("Tarefas agendadas", sev, $"Tarefa com comando suspeito: {t.Path}", Text.Clip(command),
                    s.Reason + (s.Decoded is null ? "" : $" Texto decodificado: {s.Decoded}") + (t.Enabled ? "" : " (tarefa desativada)"),
                    "Abra o Agendador de Tarefas, localize a tarefa e veja Ações, Autor e Disparadores.",
                    "Se não reconhecer, desative a tarefa no Agendador e rode uma verificação completa do Defender."));
            }
            var zone = CommandHeuristics.Zone(Environment.ExpandEnvironmentVariables(CommandHeuristics.ExecutablePath(t.Execute) ?? ""));
            if (zone is PathZone.Temp or PathZone.Downloads or PathZone.Public)
                list.Add(new Finding("Tarefas agendadas", Severity.High, $"Tarefa executa arquivo de pasta temporária: {t.Path}", Text.Clip(command),
                    "Tarefas legítimas não rodam programas de Temp/Downloads/Público.",
                    "Abra o Agendador de Tarefas e confira o autor e o arquivo.", "Verifique o arquivo com o Defender e desative a tarefa se não reconhecer."));
        }
        return list;
    }

    // ------------------------------------------------------------------ proxy

    public static IReadOnlyList<Finding> Proxy(ProxyInfo p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var list = new List<Finding>();
        if (!string.IsNullOrWhiteSpace(p.AutoConfigUrl))
            list.Add(new Finding("Proxy", Severity.Medium, "Script de configuração automática de proxy (PAC) definido", Text.Clip(p.AutoConfigUrl),
                "Um script PAC decide por onde passa a navegação. Empresas usam; adware também usa para interceptar o tráfego.",
                "Configurações > Rede e Internet > Proxy > Usar script de configuração.",
                "Se você não configurou (e o PC não é da empresa), desligue a opção nas Configurações."));
        if (p.UserProxyEnabled && !string.IsNullOrWhiteSpace(p.UserProxyServer))
        {
            var local = p.UserProxyServer.Contains("127.0.0.1", StringComparison.Ordinal) || p.UserProxyServer.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            list.Add(new Finding("Proxy", local ? Severity.Low : Severity.Medium, "Proxy manual ativo para o usuário", Text.Clip(p.UserProxyServer),
                local ? "Proxy local costuma ser de antivírus, VPN ou ferramenta de depuração (Fiddler). Também pode ser adware."
                      : "Toda a navegação passa por esse servidor. Se você não configurou, alguém pode estar lendo o tráfego.",
                "Configurações > Rede e Internet > Proxy > Configuração manual.", "Se não reconhecer, desligue nas Configurações."));
        }
        if (!string.IsNullOrWhiteSpace(p.WinHttpProxy))
            list.Add(new Finding("Proxy", Severity.Low, "Proxy do sistema (WinHTTP) definido", Text.Clip(p.WinHttpProxy),
                "O proxy WinHTTP é usado por serviços do Windows (inclusive Windows Update). Comum em empresas; raro em PC de casa.",
                "Prompt de comando: netsh winhttp show proxy.", "Se não reconhecer: netsh winhttp reset proxy (como administrador)."));
        return list;
    }

    /// <summary>
    /// Lê o valor binário <c>WinHttpSettings</c> (HKLM\...\Internet Settings\Connections). Layout: versão (4), contador (4),
    /// flags (4: 1 = direto, 2 = proxy), tamanho do proxy (4), proxy ASCII, tamanho da lista de exceções (4), exceções.
    /// Retorna o servidor proxy, ou null se for acesso direto/ilegível.
    /// </summary>
    public static string? ParseWinHttpSettings(byte[]? data)
    {
        if (data is null || data.Length < 16) return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        if ((flags & 2) == 0 || len == 0 || len > 4096 || 16 + len > data.Length) return null;
        return Encoding.ASCII.GetString(data, 16, (int)len);
    }

    // ------------------------------------------------------------------ extensões forçadas por política

    private static readonly string[] OfficialStores = ["clients2.google.com/service/update2/crx", "edge.microsoft.com/extensionwebstorebase/v1/crx", "addons.mozilla.org"];

    /// <summary>Formato do valor da política: "id;url_de_atualização" ou só "id".</summary>
    public static ForcedExtension ParseForcelistValue(string browser, string scope, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var i = value.IndexOf(';', StringComparison.Ordinal);
        return i < 0 ? new ForcedExtension(browser, scope, value.Trim(), null) : new ForcedExtension(browser, scope, value[..i].Trim(), value[(i + 1)..].Trim());
    }

    public static IReadOnlyList<Finding> ForcedExtensions(IEnumerable<ForcedExtension> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = new List<Finding>();
        foreach (var x in items)
        {
            var official = x.UpdateUrl is null || OfficialStores.Any(s => x.UpdateUrl.Contains(s, StringComparison.OrdinalIgnoreCase));
            list.Add(new Finding("Navegador", official ? Severity.Medium : Severity.High,
                $"Extensão instalada à força por política ({x.Browser})", Text.Clip($"{x.Scope}: {x.Id}{(x.UpdateUrl is null ? "" : "; " + x.UpdateUrl)}"),
                official ? "Políticas que forçam extensões são para empresas. Em PC de casa, é sinal clássico de adware (a extensão não pode ser removida pelo navegador)."
                         : "A extensão vem de fora da loja oficial e não pode ser removida pelo navegador. Forte sinal de adware/sequestrador.",
                $"No navegador, abra {(x.Browser == "Edge" ? "edge" : x.Browser == "Firefox" ? "about" : "chrome")}://policy e procure ExtensionInstallForcelist.",
                "Se o PC não é gerenciado por empresa: rode verificação completa do Defender e remova a política (o NEXUS não altera políticas; use o desinstalador do programa que a criou)."));
        }
        return list;
    }

    // ------------------------------------------------------------------ exclusões do Defender

    public static IReadOnlyList<Finding> Exclusions(DefenderExclusions? x)
    {
        var list = new List<Finding>();
        if (x is null) return list;
        if (x.Hidden)
        {
            list.Add(new Finding("Defender", Severity.Info, "Exclusões do Defender só visíveis para administrador", "Leitura recusada",
                "Nas versões recentes do Windows só administradores veem a lista de exclusões (proteção contra malware que procura onde se esconder).",
                "Segurança do Windows > Proteção contra vírus e ameaças > Gerenciar configurações > Exclusões.", "Confira a lista lá."));
            return list;
        }
        var broad = new List<string>();
        foreach (var p in x.Paths)
            if (IsBroadPath(p)) broad.Add(p);
        foreach (var e in x.Extensions)
            if (e.TrimStart('.', '*').ToLowerInvariant() is "exe" or "dll" or "ps1" or "bat" or "cmd" or "vbs" or "js" or "scr" or "msi") broad.Add("*." + e.TrimStart('.', '*'));
        foreach (var pr in x.Processes)
            if (CommandHeuristics.ExecutableName(pr) is "powershell.exe" or "cmd.exe" or "rundll32.exe" or "mshta.exe" or "wscript.exe" or "cscript.exe" or "regsvr32.exe" or "explorer.exe" or "svchost.exe")
                broad.Add(pr);
        if (broad.Count > 0)
            list.Add(new Finding("Defender", Severity.High, $"{broad.Count} exclusão(ões) ampla(s) no Defender", Text.Clip(string.Join("; ", broad)),
                "Exclusões amplas (disco inteiro, AppData, Temp, *.exe, powershell.exe) cegam o antivírus. Malware adiciona exclusões assim para não ser encontrado.",
                "Segurança do Windows > Proteção contra vírus e ameaças > Gerenciar configurações > Exclusões.",
                "Remova as que você não reconhece pela Segurança do Windows (o NEXUS não altera o Defender)."));
        var total = x.Paths.Count + x.Extensions.Count + x.Processes.Count;
        if (total > 15)
            list.Add(new Finding("Defender", Severity.Medium, $"{total} exclusões no Defender", "Lista longa",
                "Muitas exclusões aumentam a área que o antivírus não olha.", "Revise a lista na Segurança do Windows.", "Mantenha só as necessárias."));
        else if (total > 0 && broad.Count == 0)
            list.Add(new Finding("Defender", Severity.Info, $"{total} exclusão(ões) no Defender", Text.Clip(string.Join("; ", x.Paths.Concat(x.Extensions).Concat(x.Processes))),
                "Exclusões pontuais são comuns (jogos, ferramentas de desenvolvimento).", "Confira se reconhece cada uma.", "Nada a fazer se forem suas."));
        return list;
    }

    private static readonly string[] BroadTails =
        [@"\Users", @"\Windows", @"\System32", @"\SysWOW64", @"\AppData", @"\AppData\Local", @"\AppData\Roaming", @"\AppData\Local\Temp", @"\Temp",
         @"\Downloads", @"\Desktop", @"\Documents", @"\ProgramData", @"\Program Files", @"\Program Files (x86)", @"\Users\Public"];

    private static readonly string[] BroadVariables = ["%USERPROFILE%", "%TEMP%", "%TMP%", "%APPDATA%", "%LOCALAPPDATA%", "%SYSTEMROOT%", "%WINDIR%", "%PROGRAMDATA%", "%PROGRAMFILES%", "%SYSTEMDRIVE%"];

    /// <summary>Exclusão que cobre uma área grande demais: raiz de disco, pastas de sistema/perfil inteiras, variáveis sozinhas, curingas na raiz.</summary>
    public static bool IsBroadPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var t = path.Trim().TrimEnd('\\', '*').TrimEnd('\\');
        if (t.Length <= 2) return true; // "C:" ou vazio
        if (BroadVariables.Any(v => t.Equals(v, StringComparison.OrdinalIgnoreCase))) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z]:\\Users\\[^\\]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))) return true; // perfil inteiro
        return BroadTails.Any(tail => t.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ IFEO / AppInit / Winlogon

    private static readonly string[] AccessibilityImages = ["sethc.exe", "utilman.exe", "osk.exe", "magnify.exe", "narrator.exe", "displayswitch.exe", "atbroker.exe"];
    private static readonly string[] SecurityImages = ["msmpeng.exe", "mpcmdrun.exe", "msseces.exe", "securityhealthsystray.exe", "taskmgr.exe", "regedit.exe", "msconfig.exe", "mrt.exe", "autoruns.exe", "procexp.exe", "procexp64.exe", "mbam.exe", "avp.exe", "avastui.exe", "ekrn.exe", "bdagent.exe"];

    public static IReadOnlyList<Finding> Ifeo(IEnumerable<IfeoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<Finding>();
        foreach (var e in entries)
        {
            var img = e.Image.ToLowerInvariant();
            var dbg = e.Debugger.ToLowerInvariant();
            // Substitutos conhecidos e legítimos: Process Explorer no lugar do Gerenciador de Tarefas; depuradores do Visual Studio.
            if (img == "taskmgr.exe" && (dbg.Contains("procexp", StringComparison.Ordinal) || dbg.Contains("systeminformer", StringComparison.Ordinal) || dbg.Contains("processhacker", StringComparison.Ordinal)))
            {
                list.Add(new Finding("Execução de imagem (IFEO)", Severity.Info, "Gerenciador de Tarefas substituído por outro programa", Text.Clip($"{e.Image} → {e.Debugger}"),
                    "Process Explorer/System Informer usam esse mecanismo quando você escolhe 'substituir o Gerenciador de Tarefas'.",
                    "Confira se foi você que ativou a substituição.", "Nada a fazer se foi intencional.", IfeoFixAction, e.Selection));
                continue;
            }
            if (dbg.Contains("vsjitdebugger", StringComparison.Ordinal) || dbg.Contains("windbg", StringComparison.Ordinal))
            {
                list.Add(new Finding("Execução de imagem (IFEO)", Severity.Low, $"Depurador configurado para {e.Image}", Text.Clip(e.Debugger),
                    "Desenvolvedores usam isso para depurar. Fora disso, não deveria existir.",
                    "Veja se você configurou um depurador.", "Remova se não for seu (reversível).", IfeoFixAction, e.Selection));
                continue;
            }
            var sev = AccessibilityImages.Contains(img) || SecurityImages.Contains(img) ? Severity.High : Severity.Medium;
            list.Add(new Finding("Execução de imagem (IFEO)", sev, $"{e.Image} é interceptado por outro programa", Text.Clip($"[{e.View}] {e.Image} → Debugger = {e.Debugger}"),
                AccessibilityImages.Contains(img)
                    ? "Interceptar ferramentas de acessibilidade (Teclas de Aderência, Utilman) é uma porta dos fundos conhecida: abre um prompt na tela de login."
                    : SecurityImages.Contains(img)
                        ? "Interceptar antivírus ou ferramentas de diagnóstico impede que eles abram: técnica usada por malware."
                        : "O valor Debugger faz o Windows abrir outro programa no lugar deste. Pouco usado fora de depuração.",
                @"Regedit: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + e.Image + " (valor Debugger).",
                "Remover o valor Debugger (o NEXUS guarda o valor e pode desfazer) e rodar verificação completa.", IfeoFixAction, e.Selection));
        }
        return list;
    }

    public static IReadOnlyList<Finding> AppInit(IEnumerable<AppInitInfo> items, bool secureBootOn)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = new List<Finding>();
        foreach (var a in items)
        {
            if (string.IsNullOrWhiteSpace(a.Dlls)) continue;
            var active = a.Load && !secureBootOn;
            list.Add(new Finding("AppInit_DLLs", active ? Severity.High : Severity.Low, $"AppInit_DLLs definido ({a.View})", Text.Clip(a.Dlls),
                active ? "AppInit_DLLs carrega essas DLLs em quase todo programa com interface. É obsoleto e muito usado por malware."
                       : "Há DLLs listadas, mas o carregamento está desligado" + (secureBootOn ? " (Secure Boot ligado bloqueia AppInit)" : "") + ". Sobra inofensiva ou tentativa bloqueada.",
                @"Regedit: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows (valores AppInit_DLLs e LoadAppInit_DLLs).",
                "Limpar a lista (reversível) e verificar os arquivos listados com o Defender.", a.View == "32 bits" ? AppInitFix32Action : AppInitFixAction));
        }
        return list;
    }

    public static IReadOnlyList<Finding> Winlogon(WinlogonInfo w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var list = new List<Finding>();
        if (w.Shell is not null && !w.Shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            list.Add(new Finding("Winlogon", Severity.High, "Shell do Windows alterado", Text.Clip($"Shell = {w.Shell}"),
                "O Windows deveria abrir só explorer.exe ao entrar. Programa extra aqui roda a cada logon com privilégio do usuário (técnica de persistência).",
                @"Regedit: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon (valor Shell).",
                "Voltar para explorer.exe (reversível) e verificar o programa extra com o Defender.", ShellFixAction));
        if (w.Userinit is not null)
        {
            var expected = w.SystemDirectory.TrimEnd('\\') + @"\userinit.exe";
            var parts = w.Userinit.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var extra = parts.Where(p => !p.Equals(expected, StringComparison.OrdinalIgnoreCase) && !p.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (extra.Count > 0 || parts.Length == 0)
                list.Add(new Finding("Winlogon", Severity.High, "Userinit com programa extra", Text.Clip($"Userinit = {w.Userinit}"),
                    "Userinit deveria ser só userinit.exe. Entradas extras rodam a cada logon (técnica de persistência).",
                    @"Regedit: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon (valor Userinit).",
                    "O NEXUS NÃO corrige este valor automaticamente: um erro aqui impede o logon. Use a Segurança do Windows (verificação offline) ou corrija com cuidado: o padrão é \"" + expected + ",\"."));
        }
        if (!string.IsNullOrWhiteSpace(w.UserShellOverride))
            list.Add(new Finding("Winlogon", Severity.High, "Shell personalizado no perfil do usuário", Text.Clip($"HKCU Shell = {w.UserShellOverride}"),
                "Um Shell no perfil do usuário substitui o Explorer só para você. Raramente legítimo (quiosques).",
                @"Regedit: HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon (valor Shell).",
                "Remover o valor (reversível) e verificar o programa com o Defender.", w.UserShellSelection is null ? null : UserShellFixAction, w.UserShellSelection));
        return list;
    }

    /// <summary>Ordena: gravidade decrescente, depois categoria.</summary>
    public static IReadOnlyList<Finding> Sort(IEnumerable<Finding> findings) =>
        findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category, StringComparer.Ordinal).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
}
