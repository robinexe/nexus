# Utilidades (02/11/2026)

Cinco ferramentas pequenas espalhadas onde o usuário procura por elas, mais seis mitos com fonte. Catálogo próprio:
`catalog/utilities.v1.json`. Testes: `tests/Nexus.Tests/Modules/UtilitiesTests.cs`.

| O que | Onde o usuário vê | Código | Escreve? |
|---|---|---|---|
| Juntar informações do travamento | Problemas › "O jogo fechou sozinho?" | `Nexus.SystemTools/Crash/*`, `ViewModels/Simple/CrashReportPanel.cs` | Só o ZIP que o usuário salva; minidespejo por jogo pelo aplicador |
| Tempo de inicialização (tendência, antes x depois) | Ferramentas avançadas › Inicialização | `Nexus.Boot/Impact/BootTrend.cs`, `StartupPage` | Não |
| Sessão de jogo | Jogos › "Sessão de jogo" (desligada por padrão) | `Nexus.Measurement/GameSessionCap.cs`, `ViewModels/Simple/GameSessionPanel.cs` | Sim, pelo aplicador, desfeito sozinho |
| Teste do mouse | Jogos › "Teste do mouse" | `Nexus.Measurement/MousePolling.cs`, `Windows/RawMouseInput.cs`, `Views/Modules/Mouse/MouseTestWindow.cs` | Não |
| Arquivos duplicados | Ferramentas avançadas › Espaço em disco | `Nexus.DiskTools/Analysis/DuplicateFinder.cs`, `ViewModels/Modules/DuplicatesPanel.cs` | Sim, quarentena pelo aplicador |

## 1. Travamentos de jogo

**Lê (só leitura, sem administrador quando o Windows deixa):** log Application, eventos 1000 (Application Error: programa,
versão, módulo, código da exceção, caminho) e 1001 (Windows Error Reporting: AppHangB1, LiveKernelEvent…); log System, evento
4101 do provedor Display (o driver de vídeo parou de responder e se recuperou, TDR); e os `Report.wer` de
`%ProgramData%` e `%LocalAppData%\Microsoft\Windows\WER\ReportArchive|ReportQueue` dos últimos 30 dias (até 300 pastas, 1 MB
cada). Eventos e relatórios por `EvtQuery`/`EvtRender` e leitura de arquivo; sem permissão, a nota diz o que faltou.

**Resumo (puro, `CrashDigestBuilder`):** últimos 7 dias, só jogos (lista embutida do Medir, jogos achados nas lojas na tela
Jogos, ou caminho dentro de uma biblioteca de jogos; ajudantes como `UnityCrashHandler` ficam fora). 4101 e LiveKernelEvent
117/141/1a8 são juntados (até 3 min entre eles = o mesmo reinício). Recomendações:

| Sinal | Recomendação |
|---|---|
| reinício do driver de vídeo, ou o jogo caiu dentro de um módulo do driver (nvwgf2um*, atidxx*, amdxx*, igd1*…) | atualizar o driver de vídeo (instalação limpa) |
| 3+ reinícios, 2+ jogos diferentes caindo, ou c0000374/c000001d/c0000420 | tirar overclock (placa, CPU, XMP/EXPO) e conferir temperatura |
| 2+ jogos diferentes, ou corrupção de memória/instrução inválida | testar a RAM (`mdsched`) |
| um jogo só caindo 2+ vezes fora do driver | verificar os arquivos do jogo pela loja |

**ZIP (`CrashBundle`), onde o usuário escolher (Documentos por padrão):** `LEIA-ME.txt`, `resumo.txt`, `sistema.txt` (peças e
versões: Windows, CPU, GPU + driver, RAM, disco do sistema, plano de energia — sem nome do computador, conta, IPs ou programas),
`eventos.csv` (só jogos, 4101 e LiveKernelEvent de vídeo) e `wer/NN-<tipo>.txt` (até 40 relatórios relevantes). Nomes de
entrada fixos; `C:\Users\<conta>` vira `%USERPROFILE%`; chaves `MachineName`, `UserName`, `OsInfo[…]` etc. são descartadas;
células do CSV que começam com `= + - @` ganham `'` (sem fórmula no Excel). **Nunca** entram despejos de memória (`.dmp`).

**Minidespejo por jogo** (`system.crash.localdumps@1`, handler `SystemCrashLocalDumps`, administrador): grava
`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<jogo.exe>` com `DumpType=1` e `DumpCount=3` (pasta padrão
`%LOCALAPPDATA%\CrashDumps`; `DumpFolder` não é tocado). O alvo é só o nome do executável (regex no catálogo e de novo no
handler, sem `..` nem pasta). `SafetyPolicy` permite esse caminho (não está em nenhum prefixo proibido) e não foi alterada.
Desfazer volta os valores anteriores (DWord/QWord) ou remove o que o NEXUS criou, inclusive a chave do jogo e a `LocalDumps`
se ficaram vazias e não existiam antes. Fonte: https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps

## 2. Tempo de inicialização

O leitor do evento 100/101–110 já existia (`WindowsBootEventReader`, `BootReport`). Novo: `BootTrends.Trend` (média dos
últimos 5 boots contra os 5 anteriores; diferença menor que 10% = estável, porque boot varia sozinho) e
`BootTrends.LatestComparison` (até 5 boots antes e 5 depois da mudança mais recente na inicialização feita pelo NEXUS; as
datas vêm dos dois diários, execuções com ações `boot.*` aplicadas ou desfeitas). Sem boot depois: "reinicie para ver". O log
costuma exigir administrador: a página já mostra o motivo e segue sem números. Fonte:
https://eventlogxp.com/blog/windows-boot-performance-diagnostics-1/

## 3. Sessão de jogo

Desligada por padrão; ligar pede confirmação com o texto honesto (ganho pequeno; o Windows pede permissão de administrador a
cada jogo, a menos que o NEXUS esteja aberto como administrador). Com um jogo detectado pela mesma detecção do Medir
(`MeasurePage.CurrentGame`) há 20 s, aplica `game.session.do-foreground-cap@1` — política documentada
`DOPercentageMaxForegroundBandwidth = 20` (Store/Xbox/"Verificar atualizações"; o automático em segundo plano já tem o ajuste
`sys.do.background-bandwidth.cap50`). Com o jogo fechado há 15 s, desfaz a execução pelo aplicador sem perguntar de novo
(o usuário aceitou ao ligar). A execução fica anotada em `%LocalAppData%\NEXUS\game-session.json`: se o NEXUS fechar com o
jogo aberto, desfaz na próxima abertura. Recusa do aviso do Windows = não tenta de novo até o próximo jogo.
O valor entrou na lista fechada `PrivacyPolicyAllowlist` (mesma chave das duas políticas da Otimização de Entrega que já
estavam lá; o teste exige que toda entrada seja usada pelo catálogo). Ao aplicar, avisa se Steam, Epic, OneDrive, Backup do
Windows, Battle.net, EA, Ubisoft ou GOG transferiram mais de 1 MB/s em 2 s (`GetProcessIoCounters`, acesso limitado). O NEXUS
não mexe nesses programas. Fontes: https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference ,
https://www.pcgamer.com/latest-windows-10-update-stops-future-updates-interrupting-your-game

## 4. Teste do mouse

Janela própria, Raw Input (`RegisterRawInputDevices` só para ela, sem `RIDEV_INPUTSINK`: nada é lido fora dela). Cada WM_INPUT
com movimento anota `Stopwatch.GetTimestamp()`. Intervalos acima de 40 ms (mouse parado) ficam fora; precisa de 200. Taxa =
1000 / mediana; taxa padrão mais próxima (125…8000) se estiver a até 25%; irregularidade = desvio padrão e p99. Estável se
p99 ≤ 2,5× a mediana e desvio ≤ 0,5× a mediana. Limites ditos na tela: o instante inclui o atraso de entrega do Windows e,
acima de ~2000 Hz, o Windows pode juntar relatórios.

## 5. Arquivos duplicados

Na pasta aberta do analisador (ou na unidade escolhida): lista sem seguir links (`ReparsePoint` pulado), sem arquivos de
sistema nem só na nuvem, a partir de 1 MB; agrupa por tamanho → SHA-256 dos primeiros 64 KB → SHA-256 do arquivo inteiro em
blocos de 128 KB (leitura compartilhada, cancelável). Fora da busca: `Windows`, `Program Files*`, `ProgramData`, `AppData`,
`$Recycle.Bin`, `System Volume Information`, `Recovery`, `Windows.old`, bibliotecas de jogos (`steamapps`, `Epic Games`,
`XboxGames`, `WindowsApps`…), `node_modules`, `.git` e os dados do NEXUS. Mover usa `files.duplicate.quarantine@1`: o mesmo
handler `ScannerQuarantine` do Verificar arquivos (vaga de usuário), que revalida pasta, links e o SHA-256 antes e depois de
mover; nada é apagado, Desfazer devolve. A seleção nunca leva todas as cópias de um grupo; "Marcar as cópias extras" deixa a
mais antiga.

## 6. Mitos (`rejected` em `catalog/utilities.v1.json`)

| id | Por quê | Fonte |
|---|---|---|
| `myth.util.interrupt-moderation-off` | moderação soma µs; o padrão foi o melhor equilíbrio | https://github.com/djdallmann/GamingPCSetup |
| `myth.util.faster-plan-fixes-lag-under-load` | é bufferbloat; resolve com SQM no roteador | https://blog.oppedahl.com/do-you-have-bufferbloat/ |
| `myth.util.raise-tdrdelay` | só esconde o 4101; o travamento continua | https://learn.microsoft.com/en-us/archive/technet-wiki/1462.event-id-4101-display-driver-timeout-detection-and-recovery |
| `myth.util.event-37-overheating` | limite do firmware pode ser energia/corrente | https://www.intel.com/content/www/us/en/support/articles/000090904.html |
| `myth.util.8000hz-always-better-or-worse` | sem medição publicada; meça (Teste do mouse) | https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input |
| `myth.util.priority-affinity-fps-everywhere` | agendador já prioriza o primeiro plano; evidência fraca só em X3D | https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities |

## O que NÃO faz (e por quê)

- Não lê nem empacota despejos de memória (podem conter dados pessoais); não envia nada para lugar nenhum.
- Não aumenta `TdrDelay` (mito acima) nem mexe em resolução de timer.
- Não pausa Steam/Epic/OneDrive: só avisa (são programas do usuário).
- Não muda a taxa do mouse (é do programa do fabricante).
- Não apaga duplicados: só quarentena.

## Conferir no Windows

Problemas › "Juntar informações" num PC com travamentos reais (conferir contagem contra o Visualizador de Eventos e o ZIP);
"Guardar detalhes" e Desfazer (regedit); Inicialização com o NEXUS como administrador; sessão de jogo com um jogo aberto
(aviso do Windows, `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization` durante e depois); teste do mouse com mouses
de 125/1000/4000+ Hz; duplicados numa pasta de Downloads e Desfazer.
