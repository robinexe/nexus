namespace Nexus.Measurement;

// =====================================================================================================================
// Sessão de jogo (02/11; desde 03/11 a tela usa o Modo jogo automático, GameModeAuto.cs, que generaliza esta máquina para um
// conjunto de ações. GameSessionCap fica como a versão de uma ação só, com os testes de 02/11.)
// Sessão de jogo (desligada por padrão): com um jogo aberto, aplica um limite temporário aos downloads em primeiro plano da
// Otimização de Entrega (Microsoft Store/Xbox/"Verificar atualizações") e desfaz quando o jogo fecha. O Modo de Jogo do Windows
// já segura instalações de driver e avisos de reinício, e o indexador já recua sozinho desde o 2004: o que sobra é pouco, e a
// interface diz isso. Também avisa quando Steam/Epic/OneDrive/Backup estão baixando ou sincronizando agora (o NEXUS não mexe
// neles: só avisa). Esta parte é a lógica pura (testada); a leitura de E/S por processo fica em Windows/WindowsBackgroundIo.cs.
// =====================================================================================================================

/// <summary>O que a sessão de jogo deve fazer agora.</summary>
public enum GameSessionCommand { None, Apply, Restore }

/// <summary>
/// Decide quando aplicar e quando desfazer, com folga para não piscar: o jogo precisa estar aberto há
/// <see cref="StartDelay"/> (lançadores e telas de carregamento abrem e fecham processos) e fechado há <see cref="StopDelay"/>
/// (jogos que reiniciam a si mesmos). Uma tentativa recusada (aviso do Windows negado) não se repete até o jogo fechar.
/// </summary>
public sealed class GameSessionCap
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(15);

    private DateTimeOffset? _runningSince;
    private DateTimeOffset? _stoppedSince;
    private bool _attempted;

    /// <summary>O limite está aplicado (há uma execução para desfazer).</summary>
    public bool Applied { get; private set; }

    /// <param name="enabled">A opção está ligada na tela Jogos.</param>
    /// <param name="gameRunning">A detecção do Medir vê um jogo aberto agora.</param>
    public GameSessionCommand Next(bool enabled, bool gameRunning, DateTimeOffset now)
    {
        if (gameRunning)
        {
            _stoppedSince = null;
            _runningSince ??= now;
        }
        else
        {
            _runningSince = null;
            _stoppedSince ??= now;
            if (!Applied) _attempted = false; // jogo fechou: a próxima partida pode tentar de novo
        }
        if (Applied && (!enabled || (!gameRunning && now - _stoppedSince!.Value >= StopDelay))) return GameSessionCommand.Restore;
        if (!Applied && enabled && gameRunning && !_attempted && now - _runningSince!.Value >= StartDelay) return GameSessionCommand.Apply;
        return GameSessionCommand.None;
    }

    /// <summary>Resultado da aplicação pedida por <see cref="Next"/>.</summary>
    public void Applying(bool ok)
    {
        _attempted = true;
        Applied = ok;
    }

    /// <summary>Resultado do desfazer pedido por <see cref="Next"/> (falhou: tenta de novo na próxima volta).</summary>
    public void Restored(bool ok)
    {
        if (ok) Applied = false;
    }

    /// <summary>Retoma uma sessão que ficou aplicada (o NEXUS fechou com o jogo aberto): o próximo <see cref="Next"/> sem jogo desfaz.</summary>
    public void Resume() => Applied = true;
}

/// <summary>Programas que baixam ou sincronizam em segundo plano e disputam a internet/disco com o jogo.</summary>
public enum BackgroundApp { Steam, Epic, OneDrive, WindowsBackup, BattleNet, EaApp, Ubisoft, Gog }

/// <summary>Contadores de E/S acumulados de um processo (GetProcessIoCounters: arquivo, rede e dispositivos).</summary>
public sealed record ProcessIo(int Pid, string Name, ulong Bytes);

/// <summary>Um programa transferindo agora, com a taxa medida entre duas leituras.</summary>
public sealed record BackgroundActivity(BackgroundApp App, double MegabytesPerSecond);

public static class BackgroundActivityRules
{
    /// <summary>Processo → programa. Só os que de fato fazem o download/sincronização (não os ajudantes de interface).</summary>
    public static readonly IReadOnlyDictionary<string, BackgroundApp> Processes = new Dictionary<string, BackgroundApp>(StringComparer.OrdinalIgnoreCase)
    {
        ["steam.exe"] = BackgroundApp.Steam,
        ["epicgameslauncher.exe"] = BackgroundApp.Epic,
        ["onedrive.exe"] = BackgroundApp.OneDrive,
        ["filehistory.exe"] = BackgroundApp.WindowsBackup,
        ["sdclt.exe"] = BackgroundApp.WindowsBackup,
        ["battle.net.exe"] = BackgroundApp.BattleNet,
        ["agent.exe"] = BackgroundApp.BattleNet,
        ["eadesktop.exe"] = BackgroundApp.EaApp,
        ["eabackgroundservice.exe"] = BackgroundApp.EaApp,
        ["upc.exe"] = BackgroundApp.Ubisoft,
        ["galaxyclient.exe"] = BackgroundApp.Gog,
    };

    /// <summary>Abaixo disto é atividade normal de programa aberto (verificar atualização, ícone na bandeja).</summary>
    public const double BusyMegabytesPerSecond = 1.0;

    /// <summary>Quem transferiu mais que <see cref="BusyMegabytesPerSecond"/> entre as duas leituras (o mesmo PID nas duas).</summary>
    public static IReadOnlyList<BackgroundActivity> Busy(IReadOnlyList<ProcessIo> before, IReadOnlyList<ProcessIo> after, double seconds)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (seconds <= 0) return [];
        var first = before.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var rates = new Dictionary<BackgroundApp, double>();
        foreach (var p in after)
        {
            if (!Processes.TryGetValue(p.Name, out var app) || !first.TryGetValue(p.Pid, out var b) || p.Bytes < b.Bytes) continue;
            var mbps = (p.Bytes - b.Bytes) / 1_048_576.0 / seconds;
            rates[app] = rates.GetValueOrDefault(app) + mbps;
        }
        return [.. rates.Where(r => r.Value >= BusyMegabytesPerSecond).OrderByDescending(r => r.Value).Select(r => new BackgroundActivity(r.Key, r.Value))];
    }
}
