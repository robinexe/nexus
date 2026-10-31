# NEXUS Performance Lab — Arquitetura (v0.1)

## Stack

- **.NET 10 (LTS)**, C#, `net10.0` para bibliotecas puras e `net10.0-windows10.0.19041.0` para o que toca o Windows.
- **WPF + MVVM** (CommunityToolkit.Mvvm). WPF em vez de WinUI 3: implantação sem MSIX, gráficos maduros (ScottPlot), e o app não precisa de APIs exclusivas do WinUI.
- **SQLite** (`Microsoft.Data.Sqlite`) em modo WAL para diário, sessões e resultados.
- **C++** apenas em `Nexus.Native.NvApi` (shim fino sobre NVAPI DRS), justificado porque as structs DRS com uniões e strings fixas são frágeis em P/Invoke.
- Captura de frames: **PresentMon 2.6.0** como processo filho, versão fixada por SHA-256.
- Eventos do sistema: **Microsoft.Diagnostics.Tracing.TraceEvent** (sessão ETW de kernel com limites de buffer) e **WPR** com perfil `.wprp` próprio para o nível C.

## Módulos e dependências

```
Desktop (WPF, sem elevação) ─┬─> Core
                             ├─> Diagnostics ─> TraceAnalysis ─> Telemetry
                             ├─> OptimizationEngine ─> Core
                             ├─> Experimentation ─> OptimizationEngine, TraceAnalysis
                             ├─> Reports
                             └─> BrokerClient ══(named pipe, ACL)══> PrivilegedBroker (serviço elevado)
                                                                       ├─> WindowsActions
                                                                       ├─> DriverProfiles
                                                                       └─> Persistence (diário)
GameProfiles (parsers de CFG/INI; roda sem elevação, arquivos do usuário)
HardwareDiscovery (WMI/CIM, SetupAPI, DXGI, PDH — sem elevação; partes no broker)
Recovery.Cli (executável independente; lê o diário; fala com o broker ou executa handlers diretamente se elevado)
```

| Projeto | Responsabilidade | Elevado? |
|---|---|---|
| `Nexus.Core` | Contratos: `ActionDefinition`, `IActionHandler`, estados, evidência, diffs tipados, resolvedor de estado desejado | — |
| `Nexus.Persistence` | Diário SQLite, migrações versionadas, lock de execução única | ambos |
| `Nexus.WindowsActions` | Handlers tipados: registro, serviço, tarefa agendada, plano de energia (`PowerReadACValueIndex`/`PowerWriteACValueIndex`), inicialização, preferência gráfica por app, QoS de processo | broker |
| `Nexus.DriverProfiles` | NVIDIA DRS (IDs públicos); AMD/Intel: somente leitura até haver API documentada | broker |
| `Nexus.GameProfiles` | VALORANT (`GameUserSettings.ini`), CS2 (`.cfg`, `cs2_video.txt`, launch options em `localconfig.vdf` somente leitura) | não |
| `Nexus.Telemetry` | Nível A (PDH a 1 Hz), B (PresentMon + PDH), C (ETW kernel/WPR com orçamento) | B/C via broker |
| `Nexus.TraceAnalysis` | Parsers, linha do tempo unificada em QPC, janela ao redor de picos | — |
| `Nexus.Diagnostics` | Classificadores explicáveis de gargalo, investigador de stutters, níveis de evidência | — |
| `Nexus.Experimentation` | Desenho experimental, estatística, orçamento, critérios de parada | — |
| `Nexus.Reports` | HTML + JSON com fórmulas | — |
| `Nexus.Assistant` | IA opcional; emite `AiProposal` validado pelo motor | — |

## Fronteira de privilégio

O desktop **nunca** executa nada elevado. O broker é um serviço Windows (`NexusBroker`, conta LocalSystem, início manual) que:

1. Abre o pipe `\\.\pipe\NexusBroker.v1` com DACL: SYSTEM + Administradores (total), **SID do usuário interativo que instalou** (leitura/escrita). `PIPE_REJECT_REMOTE_CLIENTS`.
2. Identifica o cliente pelo token do pipe (impersonação *Identification*): ações de HKCU vão para `HKU\<SID do cliente>`,
   e o SID fica no diário (B10, implementado). O cliente confere que o dono do pipe é SYSTEM/Administradores (B3,
   implementado). **Pendente:** verificar a assinatura Authenticode do executável cliente (`GetNamedPipeClientProcessId`
   → caminho da imagem → assinatura igual à do broker), que exige certificado de assinatura de código.
3. Aceita só mensagens do **enum fechado** `BrokerOperation` (JSON com esquema; tamanho máximo 64 KiB). Não existe operação "executar comando".
4. Cada operação recebe `ActionId` + `HandlerKind` + parâmetros tipados; o broker **revalida** contra o catálogo assinado que ele mesmo carrega (o cliente não envia caminhos de registro livres).
5. Aplica a lista de **alvos proibidos** independente do catálogo (ex.: `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard`, chaves de Defender, Vanguard `vgc`/`vgk`, BCD, serviços de anticheat).

### Modelo de ameaças (resumo STRIDE)

| Ameaça | Vetor | Mitigação |
|---|---|---|
| Elevação via broker | Processo malicioso do usuário envia operação | DACL + verificação de assinatura do cliente + enum fechado + revalidação contra catálogo assinado |
| Catálogo adulterado | Usuário/malware edita JSON | Catálogo assinado (Ed25519, chave pública embutida); falha de assinatura = catálogo de fábrica |
| Path traversal / symlink em arquivos de jogos | Junção apontando para `C:\Windows` | Resolver caminho final (`GetFinalPathNameByHandle`), exigir que esteja sob a raiz do jogo detectada; recusar reparse points |
| Injeção de prompt na IA | Nome de processo/log com instruções | IA só recebe dados como conteúdo citado; saída só aceita `AiProposal` com IDs do catálogo; nunca executa texto |
| Negação de serviço | Captura ETW enorme | Orçamento de duração/tamanho por nível; quota global de traces com expiração |
| Repúdio | "Não fui eu que mudei" | Diário append-only com hash encadeado por execução |

## Ciclo de cada ação

`Detect → Assess → Plan → Backup → Apply → Verify → Measure → Revert`

Estados no diário: `Planned → BackedUp → Applying → Applied → Verified` e terminais `Reverted`, `Failed`, `RevertConflict`.

Regras invariantes (implementadas em `Nexus.Core` e cobertas por testes):

- Backup registra **existência** do valor; restaurar "ausente" = apagar o valor, nunca escrever um padrão presumido.
- `Applied` só vira `Verified` após **releitura** igual ao estado desejado.
- Antes de reverter, o estado atual é comparado ao `AppliedState`; se diferente, o resultado é `RevertConflict` (o usuário ou outro software mudou depois) e nada é sobrescrito sem confirmação.
- Valores impostos por política (`HKLM\SOFTWARE\Policies`, GPO) → estado `Blocked`; o motor não disputa.

### Instâncias e lote (núcleo v2, 31/10)

Uma ação do catálogo pode aparecer **várias vezes no mesmo pedido** com alvos diferentes (remoção em lote: várias sobras,
redes Wi-Fi, drivers, apps). A identidade de cada item é a **instância** = (id da ação, alvo resolvido)
(`ActionInstance.InstanceKey`; o alvo inclui os `userParameters` já validados e, no serviço, o `userSid`).

- **Duplicata exata** (mesma ação, mesmo alvo — mesmo que com outro valor desejado) é recusada: no broker
  (`invalid-request`, antes de travar ou gravar qualquer coisa) e no `DesiredStateResolver` (`ArgumentException`).
- **Dependências e conflitos** continuam por id de ação: depender de X exige que **todas** as instâncias de X no plano
  estejam pendentes/configuradas (e ao menos uma exista); se uma instância de X falha na execução, quem depende de X é
  bloqueado; conflitar com X bloqueia se qualquer instância de X estiver ativa. A ordem topológica é por instância.
- **Diário por instância**: cada instância tem sua entrada (backup, verificação, compensação). Falha de uma instância não
  desfaz as outras (cada uma é compensada sozinha). `RevertRun` desfaz todas, em ordem inversa; a recuperação trata cada
  entrada interrompida isoladamente (alvos diferentes não se "superam").
- **Resultados**: `ExecutionResult.Target`/`RevertResult.Target` (alvo sem o SID) e, no protocolo,
  `ActionResultDto.Index` (posição do pedido; em ReadState/Plan/Execute) e `Target`. Até 64 ações por pedido e até 64
  resultados por resposta; se não couber em 64 KiB, os textos são encurtados (nunca um item é descartado).
- **Chaves antigas** (`"supersedes"` no catálogo): uma ação pode absorver chaves `id@versão` que deixaram de existir (ex.:
  as 16+16 vagas `programs.leftover.{user|machine}.NN@1`). Elas **não podem ser pedidas**; servem só para o
  `CatalogBoundHandler` validar a restauração de entradas antigas do diário contra a ação nova (mesmo handler, mesmo alvo
  reconstruído, mesmas propriedades de segurança). Uma chave antiga só pode ter uma dona e nunca pode ser ação viva.

### Ações efêmeras (`PersistenceKind.Ephemeral`)

Operações **sem estado anterior a guardar** (limpar cache DNS/ARP, exportar drivers, criar ponto de restauração):

- o resolvedor não chama `Detect` (o item é sempre pendente quando aplicável); o executor grava `Planned → BackedUp`
  (sem original) `→ Applying`, executa, e grava `Applied → Verified` se o handler não lançar (sem releitura). Falha →
  `Failed` sem backup, que não fica pendente para a recuperação;
- **não disparam o ponto de restauração automático** (`BeforeSystemChange` só para Admin + `Persistent`) e **não prendem
  sessão** (só `Session` prende);
- desfazer responde `NothingToRevert` com a mensagem explícita `ActionExecutor.EphemeralNothingToUndo`, **sem chamar o
  handler**; a entrada é encerrada como `Accepted` (ou `Reverted`, se parou antes de executar);
- a recuperação encerra entradas interrompidas da mesma forma (aceitas, nada a desfazer) e a recuperação conta como completa;
- o catálogo recusa `Ephemeral` + `irreversible`. A decisão "efêmera" vem da definição gravada no diário: um diário
  adulterado só conseguiria impedir uma restauração (nada é escrito), nunca provocar uma escrita; e o `CatalogBoundHandler`
  recusa restaurar quando `Irreversible`, `Persistence` ou `Privileges` do diário diferem do catálogo.

### Mover arquivos sem seguir junções (`SafeFileMover`)

Quarentenas no mesmo volume movem por handle: pastas-mãe abertas com `FILE_FLAG_OPEN_REPARSE_POINT |
FILE_FLAG_BACKUP_SEMANTICS` e sem `FILE_SHARE_DELETE` (ninguém as renomeia enquanto o NEXUS as segura), conferência de
reparse point (`GetFileInformationByHandleEx`) e do caminho final (`GetFinalPathNameByHandle`), e rename da origem pelo
próprio handle com `SetFileInformationByHandle(FileRenameInfoEx)` relativo ao handle da pasta de destino, sem substituir.
Usado pela quarentena de Programas; a Limpeza deve adotar (ver `docs/modules/cleaning.md` quando o módulo migrar).

## Protocolo do broker (v1)

> **Revisão de 2026-10-30 (implementação):** em vez de o cliente pedir passos soltos (`Backup`, `Apply`, `Verify`), o
> broker roda o motor inteiro. Operações implementadas: `Ping`, `Status`, `ReadState`, `Plan`, `Execute`, `EndSession`,
> `RevertRun` e `Recover`. O cliente envia só `actionId` ("id@versão" do catálogo do **broker**) e, quando o catálogo
> define lista de valores, a escolha. Assim o cliente nunca orquestra o diário nem nomeia alvos. Quadros com prefixo de
> tamanho (máximo 64 KiB), JSON estrito. Toda escrita passa por `IExecutionGate`: no PC físico, sempre bloqueada.
> A tabela abaixo é o desenho original, mantido como histórico.

Mensagem: `{ "v":1, "id":"<guid>", "op":"<BrokerOperation>", "runId":"<guid>", "actionId":"<id>@<ver>", "args":{...} }`
Resposta: `{ "id":"<guid>", "ok":bool, "state":{...}?, "error":{"code":"...","detail":"..."}? }`

Operações v1: `ReadState`, `Backup`, `Apply`, `Verify`, `Revert`, `StartEtwSession`, `StopEtwSession`, `ReadDrsSetting`, `Ping`.
