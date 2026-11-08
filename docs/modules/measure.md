# Medir (v2)

Página **Medir** (grupo Medir). Substitui a página antiga de captura por tempo fixo e absorve a página **Comparar**.
Medir em si é **somente leitura**: as únicas gravações são arquivos do próprio NEXUS no perfil do usuário
(`%LocalAppData%\NEXUS\medir.json` e `%LocalAppData%\NEXUS\sessoes\`). A única ação de catálogo é **opcional**:
`measure.perflog-group.join` ("Medir sem pedir permissão toda vez", abaixo), pelo aplicador, com diário e desfazer.

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

Ferramenta oficial e gratuita da Intel (MIT), <https://github.com/GameTechDev/PresentMon>. **A versão 2.6.0 acompanha o NEXUS**
(`tools/presentmon/PresentMon-2.6.0-x64.exe` no repositório, sem alteração, assinada por Intel Corporation, SHA-256
`b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`). O build da interface e da CLI copia para `<saída>\tools\PresentMon\PresentMon.exe` (+ `LICENSE.txt`); o
`publish.py`, o ZIP e o instalador levam junto (no instalador fica em `%ProgramFiles%\NEXUS\tools\PresentMon`, que só
administradores alteram). Nada é baixado em tempo de execução.

**Onde procura** (`PresentMonLocator`, `src/Nexus.Measurement/PresentMonTrust.cs`), nesta ordem: (1) a cópia embutida
`tools\PresentMon\PresentMon.exe` ao lado do NEXUS; (2) o caminho que o usuário indicou; (3) `PresentMon\` ao lado do NEXUS,
`%LocalAppData%\NEXUS\PresentMon`, o app PresentMon da Intel (`Program Files\Intel\PresentMon\PresentMonCli`), CapFrameX e
Downloads. Links/junções no lugar do arquivo contam como "não existe".

**Conferência antes de executar** (`PresentMonTrust.Verify`): o PresentMon pode se elevar (`--restart_as_admin`), então é tratado
como código que roda com privilégio. Só é executado se (a) o SHA-256 é o de uma versão oficial conhecida (hoje a 2.6.0) **ou**
(b) tem assinatura Authenticode válida (`WinVerifyTrust`, WINTRUST_ACTION_GENERIC_VERIFY_V2, sem baixar lista de revogação: só
cache local) cujo assinante principal é exatamente `Intel Corporation` (CN do certificado, lido de
`WTHelperGetProvSignerFromChain`/`WTHelperGetProvCertFromChain`). Vale o primeiro candidato aprovado. Recusado: não roda, e a
página/autoteste dizem o porquê ("não é a versão oficial conhecida e a assinatura digital não é válida…"). A conferência é
refeita a cada início de captura (imediatamente antes de iniciar o processo), não só ao abrir a página.
Com a cópia embutida presente e aprovada, a página **não mostra mais "Falta o PresentMon"**.

Por que embutido: o pedido do responsável foi "pronto para ser usado automaticamente"; baixar sozinho é proibido (regra 8), e
pedir para o usuário baixar e salvar numa pasta do perfil deixava o executável que se eleva numa pasta gravável por qualquer
programa da conta. Embutido na pasta protegida do instalador e conferido por hash/assinatura, isso some.

Linha de comando (2.x; a 1.x usa um traço só e `-dont_restart_as_admin` quando não deve se elevar):

```
--process_name <jogo.exe> (--output_stdout | --output_file <temp.csv>) --terminate_on_proc_exit --timed 14400
--terminate_after_timed --stop_existing_session --session_name NEXUS_Live --no_console_stats [--restart_as_admin]
```

* Sem permissão de rastreamento, o PresentMon precisa se elevar (`--restart_as_admin`: o Windows pergunta, o usuário aceita).
  A saída padrão de um processo elevado não chega ao NEXUS, então nesse caso o PresentMon grava num CSV temporário que o
  NEXUS lê enquanto cresce (só linhas completas). Contas no grupo **Usuários do log de desempenho** (S-1-5-32-559) ou um
  NEXUS elevado medem sem aviso, pela saída padrão.

### Medir sem pedir permissão toda vez (`measure.perflog-group.join`)

Ação de catálogo (`catalog/measure.v1.json`), administrador, uma vez. Handler `MeasureLocalGroupMember`
(`src/Nexus.Measurement/PerfLogGroup.cs`, backend `Windows/WindowsLocalGroupBackend.cs`):

| | |
|---|---|
| Quem entra | A conta de quem pede: `userSid` acrescentado pelo aplicador quando ele roda como serviço ou com outra conta (B10; alvo com `"scope": "User"`); com o aplicador elevado da própria conta, a conta do processo. Sem conta válida (`S-1-5-21-…`), recusa. Só o grupo S-1-5-32-559 é aceito (nunca Administradores). |
| Detect | `NetLocalGroupGetMembers` nível 0: a conta é membro **direto**? `Membership:Member` ou ausente. |
| Apply | Se ainda não é membro: `NetLocalGroupAddMembers` (grupo traduzido do SID, funciona em qualquer idioma) e grava a marca `HKLM\SOFTWARE\NEXUS\PerfLogGroup\<SID> = "app"`. Já membro: nada muda, sem marca. |
| Restore | Estado anterior "membro": garante que continua. Estado anterior "ausente" (foi o NEXUS que adicionou): `NetLocalGroupDelMembers` e apaga a marca. Desfazer pedido por outra conta: recusa. |
| Quando vale | Depois de sair e entrar de novo na conta (o grupo entra no token no logon). Até lá a página diz "já está no grupo; saia e entre de novo" e o PresentMon continua usando `--restart_as_admin`. |
| Risco (dito na tela) | Programas da conta passam a poder iniciar rastreamentos ETW e coletar contadores sem aviso; alguns provedores revelam atividade de outros programas. Não dá privilégio de administrador. |

A página oferece o botão "Medir sem pedir permissão toda vez" (uma confirmação + o aviso do Windows uma vez) e "Desfazer"
depois de aplicar. O instalador oferece a mesma coisa (opção **marcada**, ver `docs/08-packaging.md`), com a mesma marca, e o
desinstalador tira do grupo só as contas com a marca.
* Colunas reconhecidas por nome: `FrameTime`/`CPUBusy`/`GPUBusy` (2.1+), `MsBetweenAppStart`/`MsCPUBusy`/`MsGPUBusy` (2.0),
  `MsBetweenPresents` (1.x e `--v1_metrics`). O relógio ao vivo é a soma dos frametimes (a unidade da coluna de tempo muda com
  as opções). A partida é guardada num CSV normalizado (formato 2.x) lido pelo mesmo analisador das importações.

## Captura ao vivo robusta (03/11: "aguardando quadros" para sempre no PC real)

Relato: Acer Nitro, Windows 11, 1.0.0-ci.23, conta **fora** do grupo Usuários do log de desempenho; o mostrador e a medição ficavam
em "aguardando quadros" com qualquer jogo, inclusive Fortnite. Causas encontradas (todas tratadas):

| Causa | Por que travava | Correção |
|---|---|---|
| Processo errado | O PresentMon era filtrado por `--process_name` do executável detectado. O Fortnite abre por `FortniteLauncher.exe` → `FortniteClient-Win64-Shipping_EAC_EOS.exe` (inicializador do anticheat) → `FortniteClient-Win64-Shipping.exe` (o que desenha). Os três ficam na pasta da Epic; com dois candidatos da mesma regra e nenhum em primeiro plano, vencia o de menor PID (o inicializador), que nunca apresenta quadros. Igual em jogos Unreal (`Jogo.exe` → `Jogo-Win64-Shipping.exe`), Riot, Rockstar, FromSoftware, BattlEye (`_BE.exe`). | `GameAliases` (lista conhecida + regra genérica de sufixos `_EAC_EOS/_EAC/_EOS/_BE` e `-Win64-Shipping`); o detector prefere quem desenha; o PresentMon recebe `--process_name` repetido (detectado + quem desenha); a partida fica com o nome de quem desenha. |
| Nenhum nome bate | Jogo fora da lista, ou processo que o PresentMon sem SeDebugPrivilege vê como `<unknown>` (jogo elevado/de outra conta: o filtro por nome nunca casa — aviso do próprio PresentMon). | Sem quadros em **8 s** (ou o processo filtrado fechou sem desenhar), recomeça **sem filtro de processo** (`--exclude` para dwm, Explorer, o próprio NEXUS, navegadores) e trava num processo pelo PID (`LiveTargetPicker`): o PID do jogo → um nome esperado → o dono da janela em primeiro plano que esteja desenhando → o que mais desenha (≥ 10 quadros/s), nunca compositor, Explorer, navegador, lançador, o NEXUS ou um excluído pelo usuário. O mostrador diz "medindo X". |
| Permissão | Sem administrador e fora do grupo, o ETW recusa: `error: failed to start trace session: access denied.` + "PresentMon requires either administrative privileges or to be run by a user in the "Performance Log Users" user group" (código 6). Com "pedir permissão" ligado, o PresentMon chama o aviso do Windows (`runas`) a partir de um processo em segundo plano — durante o jogo o aviso costuma só piscar na barra de tarefas. Em ambos o NEXUS esperava calado (até 2 min, depois nada). | `PresentMonDiagnosis` lê código de saída + texto (fonte: MainThread.cpp/Privilege.cpp/CommandLine.cpp da tag v2.6.0). Sem permissão ou aviso recusado (`failed to elevate privilege: cancelled.`, código 2): estado "sem permissão" com **Liberar medição (pede permissão do Windows uma vez)** (= `measure.perflog-group.join@1`, vale depois de sair e entrar na conta) e **Medir como administrador agora** (recomeça com `--restart_as_admin`, o aviso aparece com o NEXUS na frente). Aviso ainda não respondido: "aguardando a permissão do Windows…" e, depois de 15 s, "o aviso pode estar piscando na barra de tarefas". |
| `--track_frame_type` | Opção nova da rodada anterior; uma 2.x que não a conheça imprime `error: unrecognized option` e sai (código 1). | Recusada (ou linha de comando inválida) → tenta de novo **sem** a opção, uma vez. O recomeço sem filtro também vai sem ela. Sessão ETW presa com o mesmo nome → tenta com `NEXUS_Live_<pid>`. |

**Estados** (`LiveCaptureState`, página e mostrador): procurando o jogo… · aguardando a permissão do Windows… · sem permissão para medir ·
jogo não está desenhando (minimizado?) (processo vivo, sem quadros há 3 s) · medindo X · o PresentMon parou (com a última linha de erro).
Depois de **15 s** sem quadros o mostrador acrescenta "abra o NEXUS › Medir para ver o que fazer" e a página explica o que fazer.
Processo travado que fechou: volta a procurar (jogo que reabre com outro PID); tela de abertura que para e outro processo esperado
desenhando: troca para ele.

**PresentMon elevado** (o NEXUS não consegue encerrá-lo): o recomeço não é sem filtro — é filtrado também pelo processo em primeiro plano e
por quem desenha pelo jogo, para o PresentMon continuar terminando sozinho com o jogo (`--terminate_on_proc_exit`); a nova sessão
(`--stop_existing_session`, mesmo nome) encerra a anterior. O Windows pede permissão de novo nesse caso.

**Escolher o processo do jogo** (botão em Medir): 3 s de PresentMon sem filtro numa sessão própria (`NEXUS_Probe`, não atrapalha a
captura em andamento) e a lista de quem desenhou (nome, quadros/s, PID). "Medir este" mede só aquele PID (`LiveTarget.Locked`), mesmo com
nome `<unknown>`, sem procurar outro.

**Diagnóstico para o suporte**: `%LocalAppData%\NEXUS\desktop-diag.log`, linhas `nota: medir: …` com a linha de comando do PresentMon,
cada linha de erro dele, código de saída e classificação, recomeços, o processo escolhido e cada mudança de estado.

Código: `GameAliases.cs`, `PresentMonDiagnosis.cs`, `LiveTargeting.cs` (escolha, janela de quadros por processo, procura de quem desenha),
`PresentMonLiveCapture.cs` (supervisor + executor real), testes em `tests/Nexus.Tests/Modules/MeasureLiveCaptureTests.cs` (PresentMon falso).

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

## Por que travou? (análise de travadas, nível de usuário)

Depois de cada partida medida (e de cada CSV importado), o NEXUS acha as travadas e dá a **causa provável** de cada uma. Aparece
em Medir (cartão da partida: frase, contagem por causa, as piores travadas, "como foi calculado") e em uma linha na tela simples
**Jogos**, no cartão do jogo ("12 travadas: a maioria provavelmente por compilação de shader ou carregamento — normal nos primeiros
minutos de um jogo novo"). A tela sempre diz "provável"/"provavelmente": é inferência sobre contadores, não prova.

**Colunas do PresentMon 2.6** (conferidas no README do console e nas strings do `PresentMon-2.6.0-x64.exe` embutido): o conjunto
padrão do 2.6 já traz `FrameTime, CPUBusy, CPUWait, GPULatency, GPUTime, GPUBusy, GPUWait, DisplayLatency, DisplayedTime,
AnimationError, AnimationTime, PresentMode`; com `--v2_metrics` os nomes ganham "Ms" (`MsBetweenAppStart, MsCPUBusy, MsCPUWait,
MsGPUBusy, MsGPUWait, MsAnimationError`). O parser aceita os dois. `FrameType` só existe com `--track_frame_type` (beta; precisa de
instrumentação do app/driver): o NEXUS pede essa opção **só** quando o PresentMon é uma versão de hash conhecido (hoje a 2.6.0
embutida), porque uma 2.x antiga recusaria a linha de comando inteira. Quadros gerados (FrameType diferente de vazio/NA/NotSet/
Unspecified/Application, ex.: geração de quadros do driver) ficam fora da análise. O CSV normalizado da partida passou a guardar
`CPUWait, GPUWait, DisplayLatency, AnimationError, FrameType` (colunas a mais; leitores antigos ignoram).

**Pico** (`StutterAnalyzer`, `src/Nexus.Measurement/Stutter.cs`): frametime > max(2 × mediana dos 30 quadros anteriores,
mediana + 8 ms). Picos a menos de 250 ms um do outro são **uma** travada (vale o pior quadro).

**Causa provável do pior quadro**, nesta ordem:

| Causa | Regra |
|---|---|
| Sem dados | o quadro não tem CPUBusy/GPUBusy (PresentMon 1.x, CSV de outra ferramenta); vira "outro programa" se houver um com ≥ 10% da CPU naquele momento |
| Placa de vídeo | o que GPUBusy cresceu sobre a mediana cobre ≥ metade do excesso do quadro e é ≥ o que CPUBusy cresceu |
| Outro programa | do lado da CPU, com outro processo usando ≥ 10% da CPU da máquina na amostra mais próxima (até 2,5 s) e o jogo **sem** ler ≥ 8 MB/s; a tela diz qual |
| Shader ou carregamento | do lado da CPU e (jogo lendo ≥ 8 MB/s) ou (primeiros 3 min da partida e — quando há dado de disco — o jogo lendo algo) |
| Processador/jogo | o resto do lado da CPU (inclui o jogo esperando com a GPU parada: CPUWait alto, GPUBusy normal) |

**Amostras durante a captura** (`CaptureActivitySampler`, a cada 2 s numa thread do pool): CPU por processo pela lista de
processos do Monitor (`WindowsProcessSource` + `ProcessCpuTracker`, o mesmo cálculo do Gerenciador de Tarefas; os 3 maiores com
≥ 3%, sem o jogo e o Ocioso) e leituras do jogo por `GetProcessIoCounters` (ReadTransferCount, com
PROCESS_QUERY_LIMITED_INFORMATION — o acesso do Gerenciador de Tarefas; inclui disco e outras E/S). Nada é injetado nem lido da
memória do jogo. Jogo protegido que negue até esse acesso: sem dado de disco, e a tela avisa.

**O que o modo administrador acrescentaria (não feito para as travadas):** um rastreamento ETW do kernel (FileIo/DiskIo, CSwitch) diria
quais arquivos o jogo leu e qual thread segurou o processador em cada travada. Exige administrador (sessão do kernel); o NEXUS não
pede elevação a cada partida, por isso fica no nível de usuário e diz "provável". A parte de **drivers** (DPC/ISR) existe como ação
sob demanda: ver "Atraso de drivers", abaixo.

## Atraso de drivers (DPC/ISR)

Duas camadas, honestas sobre o que cada uma consegue:

| | Sem administrador (contadores) | Com administrador (rastreamento do kernel, 10 s) |
|---|---|---|
| O que diz | QUANTO os drivers seguraram cada núcleo (veredito bom / pode causar engasgos / ruim) | QUAL driver (.sys) e a pior chamada isolada de cada um |
| Como | PDH, uma leitura por segundo, 60–180 s | ação efêmera do catálogo `measure.driver-latency.trace@1` pelo aplicador |
| Muda algo? | Não | Não (sessão ETW temporária, parada ao fim; um arquivo de resumo no perfil) |

### Contadores (aproximação de nível de usuário)

Cartão em Medir: teste de 1, 2 ou 3 minutos. Lê uma vez por segundo os contadores **documentados** do Windows
`\Processor Information(*)\% DPC Time`, `% Interrupt Time`, `DPCs Queued/sec` e `Interrupts/sec` por núcleo (PDH com nomes em
inglês, funciona em qualquer idioma; `src/Nexus.Measurement/Windows/WindowsDpcCounters.cs`). Sem administrador, sem mudar nada —
**nunca** mexe na resolução do timer (há teste que procura `timeBeginPeriod`/`NtSetTimerResolution` na dll).

| Veredito | Regra (limites do NEXUS, heurística declarada na tela, não norma) |
|---|---|
| Ruim | algum segundo com DPC + interrupção ≥ 20% de um núcleo, ou média do teste ≥ 3% |
| Pode causar engasgos | algum segundo ≥ 5%, ou média ≥ 1% |
| Bom | abaixo disso |

Mostra também o pior momento (núcleo, %, segundo), a média e a **duração média** de cada DPC/ISR (% do tempo × 1 s ÷ chamadas
por segundo; só com ≥ 20 chamadas/s). Os contadores mostram QUANTO os drivers seguram o processador, não QUAL driver, e uma chamada
isolada longa (ex.: 2 ms uma vez) some na média do segundo. **A tela diz isso com todas as letras** ("Sem administrador, o teste mostra
só o total: não dá para saber QUAL driver") e oferece o botão abaixo. Fora de "Bom" e sem investigação, lista os culpados típicos
(Wi-Fi, áudio, rede, vídeo → "Abrir Atualizar drivers").

### Investigar como administrador (10 s): `measure.driver-latency.trace@1`

É o que o LatencyMon e o xperf fazem. Ação de catálogo (`catalog/measure.v1.json`), **efêmera, administrador, só leitura**: handler
`MeasureDriverLatencyTrace` (`src/Nexus.Measurement/DriverTraceHandler.cs`), backend `Windows/WindowsKernelTrace.cs` (P/Invoke em
`advapi32`/`kernel32`, sem dependência nova), agregação pura em `DriverTrace.cs`.

| | |
|---|---|
| Sessão | Logger de sistema privado `NEXUS-DriverLatency` (`LogFileMode = EVENT_TRACE_REAL_TIME_MODE \| EVENT_TRACE_SYSTEM_LOGGER_MODE`, GUID próprio em `Wnode.Guid`, Windows 8+). Se o Windows recusar o modo privado, cai para o `NT Kernel Logger` clássico (`KERNEL_LOGGER_NAME` + `SystemTraceControlGuid`). Fonte: [Configuring and Starting a SystemTraceProvider Session](https://learn.microsoft.com/windows/win32/etw/configuring-and-starting-a-systemtraceprovider-session), [Configuring and Starting the NT Kernel Logger Session](https://learn.microsoft.com/windows/win32/etw/configuring-and-starting-the-nt-kernel-logger-session), [EVENT_TRACE_PROPERTIES](https://learn.microsoft.com/windows/win32/etw/event-trace-properties). |
| Flags | `EnableFlags = EVENT_TRACE_FLAG_IMAGE_LOAD \| EVENT_TRACE_FLAG_DPC \| EVENT_TRACE_FLAG_INTERRUPT`; `ClientContext = 1` (QPC); 8–64 buffers de 128 KB; `StartTrace` → `OpenTrace` (tempo real, `PROCESS_TRACE_MODE_EVENT_RECORD`) → `ProcessTrace` numa thread → 10 s → `ControlTrace(STOP)` → `CloseTrace`. A parada fica num `finally`: também em erro a sessão é encerrada. Sessão esquecida com o mesmo nome (processo morto) é parada e recriada. |
| Eventos | Classe MOF `PerfInfo` (`{ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}`): DPC tipos 66 (ThreadDPC), 68 (DPC), 69 (TimerDPC) e ISR tipo 67; payload `InitialTime` (entrada) + `Routine` (endereço). Duração = carimbo do evento − `InitialTime`, em ticks QPC. Fontes: [PerfInfo](https://learn.microsoft.com/windows/win32/etw/perfinfo), [DPC](https://learn.microsoft.com/windows/win32/etw/dpc), [ISR](https://learn.microsoft.com/windows/win32/etw/isr) ("use com os eventos Image para achar o módulo"). |
| Endereço → módulo | `Image_Load` (`{2cb15d1d-5fc1-11d2-abe1-00a0c911f518}`, tipos 10/3/4; `DCStart` enumera os já carregados) com `ImageBase`, `ImageSize`, `FileName`, só PID 0 (kernel) — [Image_Load](https://learn.microsoft.com/windows/win32/etw/image-load); mais [`EnumDeviceDrivers`](https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-enumdevicedrivers) + `GetDeviceDriverBaseName` (base de todos os drivers, sem tamanho). Regra (`DriverTraceAnalyzer.Resolve`): maior base ≤ endereço; com tamanho conhecido o endereço tem de caber; sem tamanho vale até a base seguinte (ou 64 MB). Sem módulo = "não resolvido", contado e mostrado quando ≥ 10%. |
| Agregação | Por arquivo: quantidade, tempo total e **pior chamada** de DPC e de ISR. Chamada > 1 s é lixo (descartada). `ntoskrnl.exe`/`ntkrnlmp.exe`/`hal.dll` vêm marcados `IsKernelCore`. |
| Culpado | O **driver** (não kernel) com a pior chamada ≥ 500 µs. Abaixo disso ninguém é apontado ("nenhum driver segurou um núcleo por mais de 0,5 ms"). Top 5 por pior chamada, kernel incluído e marcado. |
| Resultado → tela | O protocolo do aplicador devolve só o veredito curto de uma ação efêmera, então o handler grava `%LocalAppData%\NEXUS\driver-latency\ultimo.json` **do perfil de quem pediu** (ProfileList pelo SID — `userSid` que o aplicador acrescenta com `"scope": "User"`, ou a conta do processo quando é a mesma; com outra conta e sem SID, recusa antes de iniciar a sessão). A interface lê o arquivo depois do `Verified` e ignora um arquivo de antes do pedido. É o mesmo canal do PresentMon elevado (arquivo no perfil). |
| Dispositivo | `Nexus.DriverUpdates.DriverModuleLookup`: dispositivo → serviço (`SPDRP_SERVICE`, novo campo `DeviceDriver.Service`) → `ImagePath` do serviço em `HKLM\SYSTEM\CurrentControlSet\Services` → nome do .sys (sem `ImagePath`, o Windows usa `drivers\<serviço>.sys`). Sem dispositivo: descrição do arquivo (`FileVersionInfo`). Categoria (rede/áudio/armazenamento/vídeo/USB/kernel) pela classe do dispositivo ou por nomes conhecidos. |
| Tela | "Provável culpado: ndis.sys (Intel(R) Wi-Fi 6 AX201) — DPC máximo 1,8 ms", top 5, nota sobre ntoskrnl/hal quando aparecem, limites (amostra de 10 s; máquina virtual) e **um** conselho pela categoria, sem prometer. O veredito ruim entra em Diagnóstico/Problemas (`drivers.latency`, atenção só em "Ruim"; com culpado, a frase diz o driver e o dispositivo; sem investigação, a frase manda investigar). |

**Por que ntoskrnl.exe e hal.dll não são "culpa":** são o próprio Windows. Os DPCs deles são temporizadores do sistema, gerência de
energia e trabalho feito em nome de algum driver que pediu (ex.: `KeSetTimer` com DPC do kernel) — o rastreamento não distingue o
pedinte. Não existe "atualizar o ntoskrnl" fora do Windows Update, então a tela mostra, marca e explica, mas nunca aponta como culpado.

**Limites (ditos na tela e no catálogo):** amostra curta (10 s) sem carga controlada — um driver que atrasa de vez em quando pode
não aparecer; repetir com o problema acontecendo. Em **máquina virtual** (Hyper-V, VMware) os tempos de DPC/ISR refletem o
hipervisor, não um PC real (o CI roda numa VM: confere o mecanismo, não os números). Durante os 10 s o Windows gasta um pouco de
processador registrando milhares de eventos por segundo. Se LatencyMon/xperf estiverem com o `NT Kernel Logger` aberto e o Windows
recusar o logger privado, a sessão deles é encerrada (dito em `risks`). Só Windows 64 bits (responde NotApplicable fora disso).

**Falta testar em PC real** (a VM do CI só prova que a sessão abre, grava e fecha): um culpado de verdade (Wi-Fi baixando, áudio
estalando) comparado com o LatencyMon; que a resolução de endereço cubra os drivers (a taxa de "não resolvido" deve ficar perto de
0%); que o `DCStart` de `Image_Load` chegue no logger privado (senão `EnumDeviceDrivers` cobre, sem tamanho); e o caso do aplicador
como serviço (SID vindo do pedido).

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

* Não baixa o PresentMon (regra: sem downloads automáticos): a 2.6.0 oficial vem junto; versões mais novas entram por commit
  (novo arquivo em `tools/presentmon` + hash em `PresentMonTrust.KnownSha256`) ou por um PresentMon assinado pela Intel que o
  usuário já tenha.
* Não executa PresentMon que não passe na conferência (hash conhecido ou assinatura da Intel), nem se o usuário indicar.
* Não injeta overlay no jogo (RTSS/Steam overlay fazem isso; é o que anticheats bloqueiam ou banem).
* Não adiciona a conta ao grupo "Usuários do log de desempenho" sem o usuário pedir (botão ou opção do instalador).
* Não detecta mapa/cena/configuração gráfica do jogo (não há fonte confiável sem ler o jogo); a página avisa.
* Não usa ETW direto sem o PresentMon: exigiria reimplementar o rastreamento de apresentação (DxgKrnl/DWM/Present) e os
  mesmos privilégios; o PresentMon é a implementação de referência da Intel.

## Arquivos

* `src/Nexus.Measurement/`: `Stutter.cs` (Por que travou?), `CaptureActivity.cs` (amostras durante a captura),
  `DriverLatency.cs` + `Windows/WindowsDpcCounters.cs` (Atraso de drivers, contadores), `DriverTrace.cs` (agregação DPC/ISR por módulo + arquivo de resultado),
  `DriverTraceHandler.cs` + `Windows/WindowsKernelTrace.cs` (ação `measure.driver-latency.trace`, sessão ETW), `Windows/WindowsProcessIo.cs`, `PresentMonTrust.cs` (localizador com o embutido primeiro, conferência hash/assinatura),
  `PresentMonSmokeTest.cs` (captura curta do autoteste), `PerfLogGroup.cs` (ação do grupo), `Windows/WindowsPresentMonTrustProbe.cs`
  (WinVerifyTrust + assinante), `Windows/WindowsLocalGroupBackend.cs` (NetLocalGroup* + marca), `GameLibrary.cs` (bibliotecas), `GameDetection.cs` (regras e máquina de estados), `PresentMonLive.cs`
  (parser em tempo real, linha de comando, localização), `PresentMonLiveCapture.cs` (processo + leitura do arquivo),
  `LiveStats.cs`, `AutoCompare.cs` (diário → mudanças, referência, avisos, trechos, veredito, biblioteca/CSV/relatório),
  `AutoMeasure.cs` (motor), `MeasurePreferences.cs` (preferências, atalho), `Overlay.cs` (estado do mostrador),
  `Windows/WindowsProcessProbe.cs`, `Windows/OverlayNative.cs`.
* `src/Nexus.Desktop/ViewModels/Modules/MeasurePage.cs`, `MeasureRows.cs`, `MeasureStutter.cs` (frases), `MeasureDriverLatency.cs` (painel) +
  `MeasureDriverTraceText.cs` (frases da investigação, lógica pura), `src/Nexus.DriverUpdates/DriverModules.cs` (.sys → dispositivo/categoria),
  `MeasureActivitySource.cs`, `Views/Pages/Measure.xaml`,
  `Views/Modules/Measure/OverlayWindow.cs` (mostrador + atalho), `Sparkline.cs`.
* Testes: `tests/Nexus.Tests/Modules/MeasureStutterTests.cs` (CSVs sintéticos do 2.6, causas, amostrador, DPC sintético),
  `MeasureDriverTraceTests.cs` (eventos DPC/ISR sintéticos → agregação, culpado, kernel marcado; handler com backend falso; .sys →
  dispositivo; achado do Diagnóstico nos 3 idiomas; frases da tela; leitura da resposta do aplicador),
  `tests/Nexus.IntegrationTests/Modules/MeasureIntegrationTests.cs` (VM do CI: a ação abre a sessão, grava o resumo e fecha),
  `tests/Nexus.Tests/Modules/MeasureTests.cs`, `MeasurePresentMonTests.cs` (localização, conferência com falsos, recusa
  antes de executar, handler do grupo com backend falso, catálogo, interface sem escrita direta) e `MeasurementTests.cs`.
* Autoteste: "Medir · PresentMon encontrado?" mostra origem (embutido/configurado/achado), conferido e assinante; "Medir ·
  PresentMon executa" (captura de 5 s do dwm.exe, CSV temporário apagado em seguida) só com `--with-benchmarks`, porque é a única
  verificação do Medir que inicia processo e grava arquivo (o teste de proibição de escrita do autoteste olha só a dll do
  autoteste; a execução fica na biblioteca do Medir, por isso a regra é a opção explícita). Sem permissão de rastreamento, é
  ignorada (o autoteste nunca pede o aviso do Windows).

## Como validar no Windows

1. Instale pelo instalador (ou rode da pasta do build). Abra Medir: o PresentMon aparece verde, "PresentMon 2.6.0 (acompanha o
   NEXUS)", com "PresentMon 2.6.0 oficial da Intel (SHA-256 conferido)"; "N jogos nas bibliotecas" lista as lojas instaladas.
   `nexus selftest --only Medir --with-benchmarks` mostra a origem "embutido", conferido, e a captura de 5 s.
   Para ver a recusa: copie qualquer outro .exe por cima de `tools\PresentMon\PresentMon.exe` numa pasta portátil e clique em
   "Procurar de novo": "PresentMon recusado" com o motivo.
1b. Se a conta não está no grupo: "Medir sem pedir permissão toda vez" → confirme → aviso do Windows → saia e entre de novo; o
   próximo jogo é medido sem aviso. "Desfazer" (ou desinstalar, se foi o NEXUS que adicionou) tira do grupo.
2. Ligue "Medir automaticamente" e abra um jogo (ex.: CS2). Em ~8 s o título vira o nome do jogo; aceite o aviso do Windows
   do PresentMon (se sua conta não estiver no grupo). Os números ao vivo e o traço aparecem.
3. Ligue o mostrador; com o jogo em janela sem bordas, o mostrador aparece no canto escolhido e cliques passam por ele.
   Ligue o atalho, use Ctrl+Shift+F (ou outro) com o NEXUS minimizado.
4. Feche o jogo depois de 2+ minutos: a partida aparece em "Jogos medidos". Aplique algo pelo NEXUS (ex.: Otimizar agora),
   volte em Medir: o convite aparece. Jogue de novo: o cartão "Antes x depois" mostra o veredito, as mudanças e os avisos.
5. Teste em tela cheia exclusiva: o mostrador não aparece (esperado, documentado).
6. Por que travou?: jogue 3+ minutos de um jogo novo (ou com o cache de shaders apagado); no cartão da partida, "Por que travou?"
   deve apontar "shader ou carregamento" nos primeiros minutos. Rode uma verificação do Defender durante uma partida: as travadas
   devem apontar "outro programa (MsMpEng)". Na tela Jogos, o cartão do jogo mostra a frase de uma linha.
7. Atraso de drivers: Medir → "Atraso de drivers" → 1 min. Num PC calmo, "Bom"; com Wi-Fi baixando muito ou áudio estalando,
   compare com o LatencyMon (que diz o driver) para calibrar os limites de 5%/20%. Depois "Investigar como administrador (10 s)":
   confirme, aceite o aviso do Windows; em ~10 s aparece "Provável culpado: <driver.sys> (<dispositivo>) — DPC máximo X ms" e o top 5
   (ntoskrnl.exe marcado como Windows). Confira com o LatencyMon ("Drivers" → highest execution) que o driver é o mesmo e que a
   sessão `NEXUS-DriverLatency` não existe mais (`logman query -ets`). Com veredito "Ruim", o Diagnóstico e Problemas mostram o achado.
