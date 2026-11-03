# Modo jogo automático (03/11/2026)

Tela **Jogos › "Modo jogo automático"**, desligado por padrão. O usuário liga uma vez, com confirmação, e escolhe em "O que ele
faz" o conjunto de ações. Quando um jogo abre, o NEXUS aplica o conjunto pelo aplicador; quando o jogo fecha, desfaz tudo
sozinho. Nada fica depois da partida. Substitui a "Sessão de jogo" de 02/11 (que fazia só o teto da Otimização de Entrega): o
teto passou a ser uma das opções do conjunto.

| Peça | Onde | Escreve no sistema? |
|---|---|---|
| Máquina de estados e lista fechada de ações | `src/Nexus.Measurement/GameModeAuto.cs` (`GameModeAutoMachine`, `GameModeActions`) | Não (lógica pura, testada) |
| Estado gravado | `src/Nexus.Desktop/ViewModels/Simple/GameSessionStore.cs` → `%LocalAppData%\NEXUS\game-session.json` | Só esse arquivo |
| Painel da tela Jogos | `src/Nexus.Desktop/ViewModels/Simple/GameModeAutoPanel.cs`, cartão em `Views/Pages/Simple.xaml` | Só por `IModuleHost` (aplicador) |
| Bandeja e Problemas | `MainViewModel` liga `GameModeAutoPanel.Notice` → `GameNotice` (balão da bandeja) e `ProblemChanged` → `SimpleProblemsPage.SetExtra` | Não |
| Testes | `tests/Nexus.Tests/Modules/GameModeAutoTests.cs`, `WiringTests.Game_mode_auto_actions_exist_with_the_right_level` | — |

## 1. Mecanismo

1. **Ligar** (um clique): caixa de confirmação listando, em palavras simples, o que será feito e a folga antes de desfazer. Sem
   essa confirmação o NEXUS nunca aplica nada: `GameSessionState.Enabled` só vira `true` depois do "Sim", e o `DispatcherTimer`
   (5 s) só roda com o modo ligado ou com execuções pendentes para desfazer.
2. **Jogo abriu**: a mesma detecção do Medir (`MeasurePage.CurrentGame`, `GameDetection` + `GameAliases`). A máquina espera o jogo
   estar aberto há **20 s** (`StartDelay`: lançadores e antitrapaça abrem e fecham processos) e devolve `Apply`.
3. **Aplicar**: `GameModeActions.Plan(escolhidas, processoElevado)` separa o pedido por nível. Um `ApplyConfirmedAsync` por nível
   (sua conta; administrador só se o NEXUS já está aberto como administrador — ver §3), com diário, releitura e tela de
   progresso, sem outra caixa de confirmação (o usuário confirmou ao ligar). As execuções gravadas (`RunId` + nível efetivo) vão
   para `game-session.json` (`PendingRuns`), junto com o nome do jogo. Se o aplicador recusar o pedido por já haver outro ajuste
   de sessão aberto (`session-active`: Laboratório A/B ou Ajustes rápidos com o plano de energia de sessão), o pedido é repetido
   sem o plano de energia, para o resto do conjunto valer. Aplicação recusada não se repete até o jogo fechar.
4. **Ativo**: a tela mostra o selo "Ativo agora: \<jogo\>" e o balão da bandeja avisa "Aplicado para \<jogo\>". Outro jogo aberto
   dentro da folga só troca o nome; o conjunto continua.
5. **Jogo fechou**: a máquina espera a **folga** (`StopDelay`, padrão 15 s, escolha de 5 s a 2 min na tela; limitada em código a
   5 s–5 min) para não desfazer num relançamento ou numa tela de carregamento que troca de processo. Se o jogo reabrir, nada é
   desfeito.
6. **Desfazer**: `RevertWithoutConfirmationAsync(runId, nível)` para cada execução gravada, da mais nova para a mais antiga —
   **pelas execuções do diário**, nunca por "valor esperado": o aplicador restaura o original que ele mesmo gravou e revalida o
   alvo antes de escrever. Sucesso: `game-session.json` fica sem pendências e a bandeja avisa "tudo voltou ao normal".
   Falha: a execução continua na lista de pendências, nova tentativa em 30 s, até 3 vezes; depois a máquina para
   (`UndoFailed`), a bandeja avisa, a tela mostra "Tentar desfazer de novo" e **Problemas** ganha a linha "O modo jogo automático
   não conseguiu desfazer a última partida" com "Abrir Histórico" — a execução aparece no **Histórico** com o botão Desfazer
   (ela continua ativa no diário).
7. **Desligar**: grava `Enabled = false` e, se algo estiver aplicado, desfaz na hora (sem esperar o jogo fechar).
8. **NEXUS fechou com o jogo aberto / PC reiniciou**: as execuções pendentes continuam em `game-session.json`. Na próxima
   abertura, `GameModeAutoMachine.Resume` retoma como "aplicado"; sem jogo aberto, desfaz depois da folga (mesmo que o usuário
   tenha desligado o modo entre uma abertura e outra). O aplicador sob demanda nunca encerra com um ajuste de sessão aberto
   (`--idle-exit` ignora sessões ativas), então o NEXUS reaberto reconecta nele; depois de um reinício do PC, o novo aplicador
   desfaz a partir do diário (o plano de energia de sessão e os valores de registro sobrevivem ao reinício, e o desfazer os
   restaura).

Formato antigo de `game-session.json` (`{"Enabled":…,"PendingRun":…}`, uma execução de administrador) continua lido: a execução
antiga entra na lista de pendências com nível administrador, mas o modo volta a **desligado** (`GameSessionState.ConfirmedOn`
exige a lista de ações gravada, que só existe depois de ligar com a confirmação deste conjunto): a "Sessão de jogo" confirmava
só o teto de downloads, e a regra é nunca aplicar sem o usuário ter ligado o modo uma vez com confirmação.

## 2. O que entra (lista fechada, em código)

Critério: rápida, reversível pelo diário, sem reinício, sem efeito que o usuário sinta depois da partida, sem aviso do Windows a
cada jogo. Nada de limpeza, serviços, registro de sistema ou qualquer coisa que "fique". A lista fica em código
(`GameModeActions.All`), não em campo do catálogo, para o teste conferir cada entrada contra o catálogo: existe, `reboot: None`,
não irreversível, não efêmera, nível igual ao declarado, sem parâmetros, com evidência, mecanismo e desfazer.

| Opção na tela | Ação do catálogo | Nível | Padrão | Fonte de que ajuda |
|---|---|---|---|---|
| Plano de energia Alto desempenho só enquanto o jogo está aberto | `power.active-scheme.high-performance.session@1` (`actions.v1.json`) | sua conta | marcada | Hipótese: o catálogo diz "requer A/B no PC alvo"; em Ryzen com o driver de chipset AMD o Balanceado já é o recomendado. Por isso é de sessão: o custo (consumo e temperatura em repouso) não fica depois da partida |
| Nenhuma notificação pop-up por cima do jogo | `sys.notifications.toasts.off@1` (`tuning.v1.json`) | sua conta | marcada | Documentação da Microsoft (`ToastEnabled`): https://support.microsoft.com/windows/8942c744-6198-fe56-4639-34320cf9444e . Não é ganho de FPS: evita o pop-up (e a troca de foco) durante a partida |
| Gravação em segundo plano da Xbox Game Bar desligada durante a partida | `win.gamedvr.background-capture.disable@1` (`actions.v1.json`) | sua conta | desmarcada | Mecanismo: captura contínua mantém o codificador de vídeo ocupado; o impacto deve ser medido (VideoBusy no PresentMon). Desmarcada por padrão porque quem usa "gravar os últimos 30 s" perde isso durante o jogo |
| Downloads da Microsoft Store e do Windows usando no máximo 20% da internet | `game.session.do-foreground-cap@1` (`utilities.v1.json`) | administrador | desmarcada | Política documentada `DOPercentageMaxForegroundBandwidth`: https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference ; só ajuda com um download da Microsoft rodando durante o jogo |

O que o Windows já faz sozinho, e por isso **não** entra: o Modo de jogo segura instalações de driver e avisos de reinício do
Windows Update (anúncio da Microsoft na atualização de outubro de 2018: https://news.xbox.com/en-us/2018/10/02/latest-october-2018-windows-update-gaming-features-3/ ;
a tela Jogos tem o "Ligar" do Modo de jogo); o indexador recua sozinho desde o 2004. Steam, Epic, OneDrive e Backup do Windows
não são tocados: ao aplicar, o painel só avisa quem está transferindo mais de 1 MB/s (`WindowsBackgroundIo`, `GetProcessIoCounters`).

## 3. Limites

- **Administrador**: a opção de administrador só fica disponível com o NEXUS aberto como administrador (o aplicador de
  administrador nasce sem aviso do Windows). Sem elevação, a caixa fica desligada com a nota e a escolha é lembrada. Nunca
  aparece um aviso do Windows no meio de uma partida.
- **Um ajuste de sessão por vez** no aplicador: com o plano de energia do modo jogo aplicado, o Laboratório A/B e os Ajustes
  rápidos não conseguem aplicar outro ajuste de sessão até o jogo fechar (o aplicador responde `session-active`).
- **Detecção**: a mesma do Medir — jogos das bibliotecas Steam/Epic, lista do usuário e mapa lançador→jogo. Jogo fora disso não
  liga o modo; o usuário pode acrescentar o executável em Medir.
- **Nada marcado**: com o modo ligado e nada marcado, nada é aplicado (a tela avisa).
- **Ganho**: pequeno e honesto; o plano de energia é hipótese até um A/B ("Comparar com e sem" na mesma tela).
- **Falta testar em máquina real**: aplicar e desfazer com um jogo de verdade (detecção + aplicador), balões da bandeja ao longo
  de uma partida, reabertura do NEXUS com pendência, reinício do PC com o plano de energia de sessão aplicado, e se
  `ToastEnabled = 0` vale na hora sem reiniciar o Explorer (a documentação da Microsoft descreve a configuração, não o momento em
  que vale). Na VM do CI (sem jogo) o painel só mostra "Desligado".

## 4. Testes

`GameModeAutoTests`: aplica depois da folga de início e desfaz depois da folga de fim; relançamento dentro da folga não desfaz;
folga configurável e limitada; desligar com o jogo aberto desfaz na hora; aplicação recusada espera o próximo jogo; outro jogo
dentro da folga troca o nome; `Resume` desfaz o pendente sem jogo (mesmo com o modo desligado); falha no desfazer tenta de novo
em 30 s e desiste na 3ª, `RetryUndo` volta a tentar; lista fechada conferida contra o catálogo; `Normalize` e `Plan` (nível,
elevação, nada marcado); `game-session.json` com escolhas, folga, pendências e formato antigo. `WiringTests` confere que a lista
fechada pede cada ação no nível certo (`DynamicSites["GameModeAutoPanel.cs|k"]`). `LocalizationTests` cobre as 39 chaves
`GameAuto.*` nos três idiomas. Não há tela nova: o cartão fica na tela Jogos, que o autoteste já abre.
