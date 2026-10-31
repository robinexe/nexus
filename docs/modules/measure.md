# Medir (v2)

Página **Medir** (grupo Medir). Substitui a página antiga de captura por tempo fixo e absorve a página **Comparar**.
Tudo aqui é **somente leitura**: nenhuma ação de catálogo, nenhuma escrita no sistema. As únicas gravações são arquivos
do próprio NEXUS no perfil do usuário (`%LocalAppData%\NEXUS\medir.json` e `%LocalAppData%\NEXUS\sessoes\`).

## O que faz

| Função | Como |
|---|---|
| Detecta o jogo aberto | A cada 2 s lê a lista de processos e a janela em primeiro plano. Regras, nesta ordem: lista do usuário → executável dentro da pasta de um jogo das bibliotecas → lista embutida de jogos populares → (opcional) janela em primeiro plano cobrindo o monitor inteiro. Exclusões do usuário e lançadores/navegadores conhecidos vencem sempre. Um jogo só conta como aberto depois de 8 s (lançadores e telas de abertura não disparam nada) e só como fechado depois de 4 s sem o processo. |
| Bibliotecas lidas | Steam (`libraryfolders.vdf`, `appmanifest_*.acf`), Epic (`%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`), Riot (`%ProgramData%\Riot Games\Metadata\*\*.product_settings.yaml`, chave `product_install_full_path`), Battle.net (`%ProgramData%\Battle.net\Agent\product.db`: protobuf sem esquema público; extraímos os caminhos de instalação como texto — heurística declarada), Xbox/Game Pass (`.GamingRoot` na raiz de cada unidade: "RGBX" + caminhos UTF-16). Arquivos com teto de tamanho, sem seguir links/junções. |
| Mede sozinho | Com "Medir automaticamente", quando o jogo abre o NEXUS inicia o PresentMon em tempo real para aquele executável; quando o jogo fecha, guarda a partida (se durou o mínimo configurado, padrão 60 s). Também dá para medir à mão ("Medir agora"/"Parar e guardar"). |
| Ao vivo | FPS do último segundo, 1% low dos últimos 10 s, frametime mediano, FPS médio da partida, gráfico rolante dos últimos 60 s com a partida de referência (ciano) por baixo e a diferença atual ("prévia"; o veredito só sai no fim). |
| Antes x depois automático | Ao guardar uma partida, compara com a referência do mesmo jogo (abaixo). Mostra veredito, métricas com ±, o que o NEXUS mudou entre as partidas (do diário) e avisos de condição diferente. Exporta relatório em texto. |
| Mostrador de FPS | Janela própria do NEXUS, transparente, sempre no topo, que não recebe cliques nem foco. Botão liga/desliga, opção "ligar sozinho ao abrir um jogo", atalho global configurável (desligado por padrão), canto da tela, CPU/GPU %/temperatura opcionais. |
| Organização | Jogos medidos com tendência (1% low por partida), partidas por jogo, "usar ou não nas comparações", marcas antes/depois (comparação manual, o antigo Comparar), limpeza de partidas antigas, exportar CSV (separador `;`, vírgula decimal), abrir a pasta. |
| Convite depois de aplicar | Se o diário tem mudança do NEXUS depois da última partida medida, a página convida: "jogue uma partida para medirmos o antes e depois" (com botão para ligar a medição automática). |

## PresentMon

Ferramenta oficial e gratuita da Intel (MIT), <https://github.com/GameTechDev/PresentMon>. **O NEXUS não baixa nada sozinho.**
Procura um executável já existente no caminho configurado, em `PresentMon\` ao lado do NEXUS, em `%LocalAppData%\NEXUS\PresentMon`,
nas pastas do app PresentMon da Intel (`Program Files\Intel\PresentMon\PresentMonCli`), do CapFrameX e em Downloads; ou o usuário indica.

Linha de comando (2.x; a 1.x usa um traço só e `-dont_restart_as_admin` quando não deve se elevar):

```
--process_name <jogo.exe> (--output_stdout | --output_file <temp.csv>) --terminate_on_proc_exit --timed 14400
--terminate_after_timed --stop_existing_session --session_name NEXUS_Live --no_console_stats [--restart_as_admin]
```

* Sem permissão de rastreamento, o PresentMon precisa se elevar (`--restart_as_admin`: o Windows pergunta, o usuário aceita).
  A saída padrão de um processo elevado não chega ao NEXUS, então nesse caso o PresentMon grava num CSV temporário que o
  NEXUS lê enquanto cresce (só linhas completas). Contas no grupo **Usuários de log de desempenho** (S-1-5-32-559) ou um
  NEXUS elevado medem sem aviso, pela saída padrão. A página explica como entrar no grupo; o NEXUS não faz isso sozinho.
* Colunas reconhecidas por nome: `FrameTime`/`CPUBusy`/`GPUBusy` (2.1+), `MsBetweenAppStart`/`MsCPUBusy`/`MsGPUBusy` (2.0),
  `MsBetweenPresents` (1.x e `--v1_metrics`). O relógio ao vivo é a soma dos frametimes (a unidade da coluna de tempo muda com
  as opções). A partida é guardada num CSV normalizado (formato 2.x) lido pelo mesmo analisador das importações.

## Estatística e veredito

* **Ao vivo**: 1% low = 1000 ÷ média dos ⌈1%⌉ maiores frametimes (mesma fórmula do resumo). Janela deslizante numa lista
  ordenada (inserção/remoção por busca binária); 1% low da partida por histograma de 0,05 ms até 250 ms + lista exata acima
  (memória fixa). O resumo salvo é calculado de novo, exato, sobre todos os quadros.
* **Referência**: sessões do mesmo jogo (executável), não excluídas e com pelo menos 60 s. Se houve mudança do NEXUS antes da
  partida atual, a referência é a última partida **antes** da mudança; senão, a partida anterior.
* **Comparação**: com 3 ou mais partidas de cada lado da mudança, compara partidas inteiras (pareado, IC 95% por bootstrap,
  `SessionComparison`). Com menos, compara **trechos equivalentes**: cada partida é cortada em trechos de 30 s, o primeiro
  (carregamento) sai quando sobra, e o mesmo número de trechos de cada partida é escolhido espaçado igualmente (começo com
  começo, fim com fim) para a comparação pareada com bootstrap (`RunComparison.ComparePaired`). A página diz que isso é
  indicativo (trechos da mesma partida não são independentes). Menos de 3 trechos: sem veredito.
* **Frases**: "Melhorou 1% low em 6,1% ± 2,0%" (± = metade do intervalo de 95%), "Piorou …", "Misto …" ou
  "Sem diferença mensurável: as diferenças cabem na variação normal entre partidas". Limiar de diferença relevante por
  métrica (FPS 3%, 1% low 5%, P99 5%, engasgos 1/min).
* **Avisos**: resolução da janela do jogo diferente, driver de vídeo diferente, tomada x bateria, modo de apresentação
  diferente (tela cheia exclusiva / sem bordas otimizada / janela), Windows atualizado, duração muito diferente, nenhuma
  mudança do NEXUS entre as partidas, e sempre: "o NEXUS não sabe o mapa nem a cena".

## Segurança e anticheat

* Processos: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName` (o mesmo acesso do Gerenciador
  de Tarefas). Não lê memória, não lista módulos, não injeta, não usa hooks. Por isso é seguro com Vanguard, Easy Anti-Cheat
  e BattlEye. Quadros vêm do ETW (eventos de apresentação do Windows) pelo PresentMon, que também não toca no jogo.
* Mostrador: janela WPF do NEXUS com `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`.
  **Limitação honesta**: em tela cheia **exclusiva** o jogo é dono da tela e nenhuma janela aparece por cima. Em janela sem
  bordas (padrão do Windows 11 com as "otimizações para jogos em janela") funciona. A janela é fechada (não só escondida)
  ao desligar, para não manter o NEXUS aberto quando a janela principal fecha.
* Atalho: `RegisterHotKey` numa janela só de mensagens (não é hook de teclado). Desligado por padrão, porque um atalho
  global "rouba" a combinação de todos os programas (Ctrl+Shift+F é "buscar em arquivos" em vários editores). Exige
  Ctrl, Alt ou Win; teclas A–Z, 0–9, F1–F24. Combinação em uso por outro programa: a página avisa.
* Diário: aberto com `SqliteJournal.OpenReadOnly` (usuário e administrador). Nada é escrito.
* Limpeza: só arquivos da pasta de sessões do NEXUS; mantém a última partida de cada jogo e as marcadas antes/depois; pede
  confirmação. Arquivos temporários de captura com mais de 1 dia são apagados ao iniciar.

## O que NÃO faz (e por quê)

* Não baixa o PresentMon (regra: sem downloads automáticos).
* Não injeta overlay no jogo (RTSS/Steam overlay fazem isso; é o que anticheats bloqueiam ou banem).
* Não adiciona a conta ao grupo "Usuários de log de desempenho" (mudança de segurança do sistema; seria uma ação de catálogo
  Admin com Detect/Apply/Restore — fica como proposta).
* Não detecta mapa/cena/configuração gráfica do jogo (não há fonte confiável sem ler o jogo); a página avisa.
* Não usa ETW direto sem o PresentMon: exigiria reimplementar o rastreamento de apresentação (DxgKrnl/DWM/Present) e os
  mesmos privilégios; o PresentMon é a implementação de referência da Intel.

## Arquivos

* `src/Nexus.Measurement/`: `GameLibrary.cs` (bibliotecas), `GameDetection.cs` (regras e máquina de estados), `PresentMonLive.cs`
  (parser em tempo real, linha de comando, localização), `PresentMonLiveCapture.cs` (processo + leitura do arquivo),
  `LiveStats.cs`, `AutoCompare.cs` (diário → mudanças, referência, avisos, trechos, veredito, biblioteca/CSV/relatório),
  `AutoMeasure.cs` (motor), `MeasurePreferences.cs` (preferências, atalho), `Overlay.cs` (estado do mostrador),
  `Windows/WindowsProcessProbe.cs`, `Windows/OverlayNative.cs`.
* `src/Nexus.Desktop/ViewModels/Modules/MeasurePage.cs`, `MeasureRows.cs`, `Views/Pages/Measure.xaml`,
  `Views/Modules/Measure/OverlayWindow.cs` (mostrador + atalho), `Sparkline.cs`.
* Testes: `tests/Nexus.Tests/Modules/MeasureTests.cs` (+ `MeasurementTests.cs` antigos continuam valendo).

## Como validar no Windows

1. Baixe `PresentMon-2.x-x64.exe` (Releases do repositório da Intel) para `%LocalAppData%\NEXUS\PresentMon\`. Abra Medir:
   o ponto do PresentMon fica verde; "N jogos nas bibliotecas" lista as lojas instaladas.
2. Ligue "Medir automaticamente" e abra um jogo (ex.: CS2). Em ~8 s o título vira o nome do jogo; aceite o aviso do Windows
   do PresentMon (se sua conta não estiver no grupo). Os números ao vivo e o traço aparecem.
3. Ligue o mostrador; com o jogo em janela sem bordas, o mostrador aparece no canto escolhido e cliques passam por ele.
   Ligue o atalho, use Ctrl+Shift+F (ou outro) com o NEXUS minimizado.
4. Feche o jogo depois de 2+ minutos: a partida aparece em "Jogos medidos". Aplique algo pelo NEXUS (ex.: Otimizar agora),
   volte em Medir: o convite aparece. Jogue de novo: o cartão "Antes x depois" mostra o veredito, as mudanças e os avisos.
5. Teste em tela cheia exclusiva: o mostrador não aparece (esperado, documentado).
