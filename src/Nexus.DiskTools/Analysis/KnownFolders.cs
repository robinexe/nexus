namespace Nexus.DiskTools.Analysis;

/// <summary>O que fazer com um item grande conhecido.</summary>
public enum InsightKind
{
    /// <summary>Não apague à mão: quebra o Windows ou programas. Há uma ferramenta oficial (ou nenhuma ação).</summary>
    DoNotTouch,
    /// <summary>Pode liberar espaço, pela ferramenta oficial indicada.</summary>
    OfficialTool,
    /// <summary>Arquivos seus: decisão sua (o NEXUS só mostra).</summary>
    YourDecision,
    /// <summary>Jogo instalado: desinstale ou mova pela loja.</summary>
    Game,
    /// <summary>Cache que se recria sozinho; apagar só adia o problema e pode custar desempenho na primeira vez.</summary>
    Cache,
}

/// <summary>Explicação para uma pasta/arquivo grande conhecido.</summary>
public sealed record FolderInsight(string Path, string Title, long Size, InsightKind Kind, string What, string Advice, string? Command = null)
{
    public string KindLabel => Kind switch
    {
        InsightKind.DoNotTouch => "Não apague à mão",
        InsightKind.OfficialTool => "Use a ferramenta oficial",
        InsightKind.YourDecision => "Decisão sua",
        InsightKind.Game => "Jogo",
        _ => "Cache",
    };
}

/// <summary>Contexto do PC para reconhecer itens (nomes dos jogos nas lojas).</summary>
public sealed class KnownFolderContext
{
    /// <summary>installdir da Steam → nome do jogo (por pasta steamapps, ou uma lista única).</summary>
    public Func<string, IReadOnlyDictionary<string, string>> SteamNames { get; init; } = GameLibraries.ReadSteamNames;
    /// <summary>Pasta de instalação completa → nome (Epic).</summary>
    public IReadOnlyDictionary<string, string> EpicNames { get; init; } = new Dictionary<string, string>();
    /// <summary>Tamanho mínimo para uma pasta comum entrar na lista.</summary>
    public long MinSize { get; init; } = 50L * 1024 * 1024;
    /// <summary>Idade usada para "arquivos antigos" (deve ser a mesma da varredura).</summary>
    public int OldFileDays { get; init; } = 90;
}

/// <summary>
/// Reconhece pastas e arquivos grandes conhecidos no resultado de uma varredura e explica, em português simples, o que são e o
/// que fazer (com a ferramenta oficial, nunca apagando à mão o que o Windows gerencia). Somente leitura.
/// </summary>
public static class KnownFolders
{
    public static IReadOnlyList<FolderInsight> Analyze(DiskScanResult result, KnownFolderContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        context ??= new KnownFolderContext();
        var list = new List<FolderInsight>();
        var steamCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in result.Root.SelfAndDescendants())
        {
            if (Match(node, context, steamCache) is { } insight) list.Add(insight);
            foreach (var f in node.LargestFiles)
                if (MatchFile(f, node) is { } fi) list.Add(fi);
        }
        return list
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(i => i.Size).ToList();
    }

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static FolderInsight? Match(DirNode n, KnownFolderContext ctx, Dictionary<string, IReadOnlyDictionary<string, string>> steamCache)
    {
        var parent = n.Parent?.Name;
        var grand = n.Parent?.Parent?.Name;
        var big = n.Size >= ctx.MinSize;

        if (Is(n.Name, "WinSxS") && Is(parent, "Windows"))
            return new(n.FullPath, "Repositório de componentes do Windows (WinSxS)", n.Size, InsightKind.DoNotTouch,
                "Guarda as versões dos componentes do Windows usadas por atualizações, reparos e recursos opcionais. Boa parte dos arquivos são " +
                "links físicos para System32: o tamanho mostrado aqui conta duas vezes o que é compartilhado, então o espaço real é menor.",
                "Nunca apague nada aqui à mão: o Windows deixa de atualizar ou de reparar. Use a Limpeza de Disco (\"Limpar arquivos do sistema\" → " +
                "\"Limpeza de atualizações do Windows\") ou o comando oficial abaixo, como administrador. Para ver o tamanho real: " +
                "Dism /Online /Cleanup-Image /AnalyzeComponentStore.",
                "Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
        if (Is(n.Name, "Installer") && Is(parent, "Windows") && big)
            return new(n.FullPath, "Cache de instaladores (Windows\\Installer)", n.Size, InsightKind.DoNotTouch,
                "Cópias dos pacotes .msi/.msp dos programas instalados. O Windows usa para reparar, atualizar e DESINSTALAR esses programas.",
                "Não apague: depois disso vários programas não desinstalam nem atualizam mais. Desinstale os programas que não usa (página Programas).");
        if (Is(n.Name, "SoftwareDistribution") && Is(parent, "Windows") && big)
            return new(n.FullPath, "Downloads do Windows Update", n.Size, InsightKind.OfficialTool,
                "Pacotes baixados pelo Windows Update. Depois de instalados, a maior parte pode sair.",
                "Use a Limpeza de Disco (\"Limpeza do Windows Update\") ou a página Limpeza do NEXUS. Não apague a pasta inteira à mão.");
        if (Is(n.Name, "Temp") && (Is(parent, "Windows") || (Is(parent, "Local") && Is(grand, "AppData"))) && big)
            return new(n.FullPath, "Arquivos temporários", n.Size, InsightKind.OfficialTool,
                "Arquivos que programas e instaladores deixam para trás. Alguns ainda estão em uso pelos programas abertos.",
                "Use a página Limpeza do NEXUS (pula o que está em uso e guarda em quarentena) ou Configurações → Sistema → Armazenamento → Arquivos temporários.");
        if (Is(n.Name, "Windows.old") && big)
            return new(n.FullPath, "Instalação anterior do Windows (Windows.old)", n.Size, InsightKind.OfficialTool,
                "Cópia do Windows anterior, guardada após uma atualização grande para permitir voltar atrás.",
                "Se o Windows atual está bom, remova pela Limpeza de Disco (\"Instalações anteriores do Windows\"). Depois disso não dá para voltar à versão anterior. " +
                "O próprio Windows apaga sozinho após cerca de 10 dias.");
        if ((Is(n.Name, "$WINDOWS.~BT") || Is(n.Name, "$Windows.~WS")) && big)
            return new(n.FullPath, "Arquivos de atualização do Windows", n.Size, InsightKind.OfficialTool,
                "Arquivos temporários de uma atualização de versão do Windows (em andamento ou já concluída).",
                "Remova pela Limpeza de Disco (\"Arquivos temporários de instalação do Windows\"). Não apague durante uma atualização.");
        if (Is(n.Name, "System Volume Information"))
            return big || n.Incomplete
                ? new(n.FullPath, "Pontos de restauração e cópias de sombra", n.Size, InsightKind.DoNotTouch,
                    "Pontos de restauração do Sistema e cópias de sombra (VSS). Normalmente o NEXUS não tem permissão para ver o tamanho real" +
                    (n.Incomplete ? " (acesso negado: o número aqui é um mínimo)." : "."),
                    "Não mexa na pasta. Para limitar o espaço: Painel de Controle → Recuperação → Configurar Restauração do Sistema → Configurar → \"Uso máximo\". " +
                    "Ver o uso: vssadmin list shadowstorage (administrador).")
                : null;
        if (Is(n.Name, "$Recycle.Bin") && big)
            return new(n.FullPath, "Lixeira", n.Size, InsightKind.YourDecision,
                "Arquivos que você apagou e que ainda podem ser recuperados.",
                "Confira a Lixeira e esvazie quando tiver certeza (clique direito na Lixeira → Esvaziar).");
        if (Is(n.Name, "Package Cache") && Is(parent, "ProgramData") && big)
            return new(n.FullPath, "Cache de pacotes (ProgramData\\Package Cache)", n.Size, InsightKind.DoNotTouch,
                "Instaladores guardados pelo Visual Studio, redistribuíveis do Visual C++ e outros programas para reparar e desinstalar.",
                "Não apague: os programas passam a falhar ao desinstalar ou atualizar.");
        if ((Is(n.Name, "Minidump") || Is(n.Name, "LiveKernelReports")) && Is(parent, "Windows") && big)
            return new(n.FullPath, "Relatórios de falhas do Windows", n.Size, InsightKind.OfficialTool,
                "Despejos de memória de travamentos (tela azul, driver de vídeo reiniciado). Úteis para diagnosticar o problema.",
                "Se não precisa investigar uma falha, remova pela Limpeza de Disco (\"Arquivos de despejo de memória de erros do sistema\").");

        // Lojas de jogos
        if (Is(parent, "common") && Is(grand, "steamapps") && n.Parent?.Parent is { } steamapps)
        {
            if (!steamCache.TryGetValue(steamapps.FullPath, out var names))
                steamCache[steamapps.FullPath] = names = SafeNames(ctx, steamapps.FullPath);
            var title = names.TryGetValue(n.Name, out var nm) ? nm : n.Name;
            return big ? Game(n, $"{title} (Steam)", "Steam") : null;
        }
        if (Is(parent, "steamapps") && Is(n.Name, "shadercache") && big)
            return new(n.FullPath, "Cache de shaders da Steam", n.Size, InsightKind.Cache,
                "Shaders pré-compilados que a Steam baixa ou gera para os seus jogos. Evitam engasgos (stutter) na primeira vez em cada cena.",
                "Não precisa apagar. Se apagar, ele volta a crescer e os jogos podem engasgar até recompilar.");
        if (Is(parent, "steamapps") && Is(n.Name, "downloading") && big)
            return new(n.FullPath, "Downloads interrompidos da Steam", n.Size, InsightKind.OfficialTool,
                "Atualizações ou instalações que a Steam começou e não terminou.",
                "Na Steam: Downloads → retome ou cancele. Ou Configurações → Armazenamento. Não apague com a Steam aberta.");
        if (Is(parent, "steamapps") && Is(n.Name, "workshop") && big)
            return new(n.FullPath, "Itens da Oficina Steam (mods)", n.Size, InsightKind.Game,
                "Mods e mapas baixados da Oficina Steam.",
                "Cancele a inscrição dos itens que não usa na página da Oficina do jogo; a Steam remove os arquivos.");
        if (Is(parent, "Epic Games") && big)
        {
            if (Is(n.Name, "Launcher")) return null;
            var title = ctx.EpicNames.TryGetValue(n.FullPath.TrimEnd('\\', '/'), out var nm) ? nm : n.Name;
            return Game(n, $"{title} (Epic Games)", "Epic Games Launcher");
        }
        if (Is(parent, "XboxGames") && big) return Game(n, $"{n.Name} (Xbox)", "aplicativo Xbox");
        if (Is(parent, "Riot Games") && big) return Game(n, $"{n.Name} (Riot)", "Riot Client");

        // Caches de shader dos drivers
        if (((Is(n.Name, "DXCache") || Is(n.Name, "GLCache")) && Is(parent, "NVIDIA")) ||
            ((Is(n.Name, "DxCache") || Is(n.Name, "DxcCache") || Is(n.Name, "VkCache")) && Is(parent, "AMD")) ||
            (Is(n.Name, "D3DSCache") && Is(parent, "Local")))
            return big
                ? new(n.FullPath, $"Cache de shaders do driver ({n.Name})", n.Size, InsightKind.Cache,
                    "Shaders compilados pelo driver de vídeo. O jogo carrega daqui em vez de compilar de novo.",
                    "Deixe como está. Apagar libera pouco e causa engasgos na próxima vez que você joga, até o cache se refazer. " +
                    "Só faz sentido limpar se um jogo tiver gráficos corrompidos depois de atualizar o driver.")
                : null;

        if (Is(n.Name, "node_modules") && !Is(parent, "node_modules") && n.Size >= Math.Max(ctx.MinSize, 300L * 1024 * 1024))
            return new(n.FullPath, "Dependências de projeto (node_modules)", n.Size, InsightKind.YourDecision,
                "Pacotes baixados por npm/yarn para um projeto. Podem ser baixados de novo com \"npm install\".",
                "Se o projeto está parado, apague a pasta node_modules dele pelo Explorer (ou com npx npkill). Ela volta quando você rodar npm install.");

        if (Is(n.Name, "Downloads") && Is(grand, "Users"))
        {
            var oldBytes = n.OldBytes;
            return n.Size >= ctx.MinSize
                ? new(n.FullPath, "Downloads", n.Size, InsightKind.YourDecision,
                    oldBytes > 0
                        ? $"{Format.Bytes(oldBytes)} são arquivos sem alteração há mais de {ctx.OldFileDays} dias (instaladores e arquivos já usados costumam ficar aqui)."
                        : "Arquivos baixados da internet.",
                    "Ordene por data no Explorer e apague o que não precisa mais (vai para a Lixeira). Instaladores (.exe/.msi) já usados podem sair.")
                : null;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> SafeNames(KnownFolderContext ctx, string steamapps)
    {
        try { return ctx.SteamNames(steamapps); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Dictionary<string, string>(); }
    }

    private static FolderInsight Game(DirNode n, string title, string store) =>
        new(n.FullPath, title, n.Size, InsightKind.Game,
            "Jogo instalado.",
            $"Para liberar espaço, desinstale pelo {store} (ou mova a biblioteca para outro disco pelo próprio {store}). " +
            "Não apague a pasta à mão: a loja continua achando que o jogo está instalado e pode baixar tudo de novo.");

    private static FolderInsight? MatchFile(FileItem f, DirNode folder)
    {
        var atRoot = folder.Parent is null && Path.GetPathRoot(folder.FullPath) is { Length: > 0 } root &&
                     string.Equals(root.TrimEnd('\\', '/'), folder.FullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        if (Is(f.Name, "hiberfil.sys") && atRoot)
            return new(f.FullPath, "Arquivo de hibernação (hiberfil.sys)", f.Size, InsightKind.OfficialTool,
                "Guarda a memória quando o PC hiberna. Também é usado pela Inicialização rápida do Windows.",
                "Se você nunca hiberna, pode desligar com \"powercfg /h off\" (Prompt como administrador): o arquivo some, mas a hibernação e a " +
                "Inicialização rápida deixam de existir (o boot a frio pode ficar alguns segundos mais lento). Meio-termo: \"powercfg /h /type reduced\" " +
                "mantém a Inicialização rápida com um arquivo menor. Para voltar: \"powercfg /h on\". Nunca apague o arquivo à mão.",
                "powercfg /h off");
        if (Is(f.Name, "pagefile.sys") && atRoot)
            return new(f.FullPath, "Arquivo de paginação (pagefile.sys)", f.Size, InsightKind.DoNotTouch,
                "Memória virtual: o Windows usa quando a RAM enche e para gravar relatórios de tela azul.",
                "Deixe o Windows gerenciar. Desligar ou encolher demais faz jogos e programas fecharem com \"memória insuficiente\" quando a RAM enche, " +
                "e impede o relatório de falhas. Se precisar mudar: Sistema → Configurações avançadas → Desempenho → Avançado → Memória virtual.");
        if (Is(f.Name, "swapfile.sys") && atRoot)
            return new(f.FullPath, "Arquivo de troca de apps (swapfile.sys)", f.Size, InsightKind.DoNotTouch,
                "Usado pelos aplicativos da Microsoft Store para suspender e retomar. Normalmente pequeno.",
                "Não mexa: é gerenciado junto com o arquivo de paginação.");
        if (Is(f.Name, "MEMORY.DMP") && Is(folder.Name, "Windows"))
            return new(f.FullPath, "Despejo de memória de tela azul (MEMORY.DMP)", f.Size, InsightKind.OfficialTool,
                "Cópia da memória gravada na última tela azul, para diagnóstico.",
                "Se não precisa investigar a falha, remova pela Limpeza de Disco (\"Arquivos de despejo de memória de erros do sistema\").");
        if (Is(f.Name, "ext4.vhdx"))
            return new(f.FullPath, "Disco virtual do WSL (ext4.vhdx)", f.Size, InsightKind.YourDecision,
                "Disco do Linux (WSL). Ele cresce quando você grava arquivos no Linux, mas não encolhe sozinho quando você apaga.",
                "Apague o que não precisa dentro do Linux e então compacte: \"wsl --manage <distro> --set-sparse true\" (WSL recente) " +
                "ou Optimize-VHD no Hyper-V. Não apague o arquivo: é o seu Linux inteiro.");
        if (Is(f.Name, "docker_data.vhdx") || (Is(f.Name, "data.vhdx") && f.FullPath.Contains("Docker", StringComparison.OrdinalIgnoreCase)))
            return new(f.FullPath, "Disco virtual do Docker", f.Size, InsightKind.YourDecision,
                "Imagens, contêineres e volumes do Docker Desktop.",
                "Rode \"docker system prune\" (e, se quiser, \"docker system prune -a\") para remover o que não está em uso. Não apague o arquivo à mão.");
        return null;
    }
}

/// <summary>Formatação de tamanhos em português (1 KB = 1024 bytes, como o Explorer).</summary>
public static class Format
{
    private static readonly System.Globalization.CultureInfo Pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes.ToString("N0", Pt)} bytes" : v.ToString(v >= 100 ? "N0" : v >= 10 ? "N1" : "N2", Pt) + " " + units[u];
    }
}
