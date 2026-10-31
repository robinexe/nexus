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
2. Verifica o cliente com `GetNamedPipeClientProcessId` → caminho da imagem → assinatura Authenticode igual à do broker.
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
