# Matriz de capacidades (atualizada na Etapa 3, 2026-10-30)

Rótulos usados aqui e na interface:
- **implementado e validado**: existe e há evidência registrada **para esta versão** no ambiente indicado;
- **implementado, não validado**: existe, mas falta a evidência;
- **não suportado neste ambiente**;
- **pendente**: não existe.

"Testado em Linux" significa testes automatizados executados e aprovados no contêiner Linux, com backends em memória,
arquivos temporários e processos auxiliares. Nenhum teste toca registro, energia ou serviços reais.

| Capacidade | Rótulo (Windows) | Evidência | Observação |
|---|---|---|---|
| Compilação + testes unitários (versão de 30/10, 55 testes) | **implementado e validado** | Windows, SDK 10.0.403, informado pelo usuário (`06-build-log.md`) | Vale só para aquela versão |
| Compilação + testes unitários (Etapa 2, 99 testes) | **implementado e validado** | Windows 30/10 20:41: 0 avisos, 99/99 | Depois disso: só a classificação de rede (106 testes em Linux) |
| `nexus-recover runs/show/scan` (somente leitura) | **implementado e validado** | Windows 30/10 20:41: exit 0/1/0/3 esperados, SHA igual | |
| Contratos, resolvedor, executor, compensação | implementado, não validado | Testado em Linux | |
| Diário SQLite (WAL, FULL, esquema 2, `Restoring`, leitura somente leitura, thread-safe) | implementado, não validado | Testado em Linux | |
| **Coordenação entre executores** (trava do SO, dono verificado por PID + início) | **implementado e validado** | 9 testes com processos reais aprovados no Windows e no Linux | Falta: ACL de `%ProgramData%\NEXUS` no instalador (R1) e cenário com usuários diferentes |
| Recuperação (A1–A7, R2–R7) | implementado, não validado | Testado em Linux e Windows (testes unitários); mutações detectadas | Decisões tomadas em 31/10 (`07-recovery-cases.md`) |
| `nexus-recover accept <entryId>` (R3) | implementado, não validado no Windows | Linux: 149/149; processos reais no teste do CLI | Só escreve no diário |
| `nexus-recover recover/revert` contra o sistema real | implementado, não validado | Nunca executado contra registro/energia/serviços reais | Só em VM Windows isolada |
| **Descoberta somente leitura**: Windows, CPU/topologia, GPU/driver, memória/SMBIOS, placa/BIOS, discos, rede, energia, recursos de jogo, serviços, inicialização | implementado, não validado | Executada no Windows sem falhas (0 indisponíveis); valores coerentes com o hardware | Falta a conferência dos valores pelo usuário; correção da rede ainda não rodou no Windows |
| Diagnóstico somente leitura | implementado, não validado | Regras testadas; "indisponível" quando nada foi lido | |
| Catálogo com estado real lido | implementado, não validado | Carregador testado com o catálogo real (13 ações, 14 rejeitadas) | |
| Simulação do plano (dry-run) | implementado, não validado | Testada com handlers embrulhados em `SimulationOnlyHandler` | |
| Interface WPF (visão geral, hardware, diagnóstico, catálogo, simulação, histórico, capacidades, exportação JSON) | implementado, não validado | Compila; garantia "sem caminho de execução" por inspeção do assembly; **abriu no Windows** (informado pelo usuário) | Falta conferir página a página os valores exibidos |
| Aplicar ações reais no Windows hospedeiro | **pendente (bloqueado de propósito)** | — | A interface não tem caminho de execução; só testes em VM |
| Broker: protocolo fechado, validação contra o próprio catálogo, sessão com trava, portão de execução | implementado, não validado | 36 testes em Linux (inclui pipe real); 2 revisões independentes | Não executado no Windows |
| Broker: host com pipe ACL (`nexus-broker`) | implementado, não validado | Compila; no PC físico serve só leitura | Pendente: serviço Windows, validação do servidor pelo cliente (B3), trava com ACL (B7), HKCU como serviço (B10) |
| Portão de execução (VM + liberação com dono administrador) | implementado, não validado | Testes em Linux; no Windows o SMBIOS do PC físico já bloqueia | |
| Testes de integração em VM (registro, energia, serviço, crash + `nexus-recover`) | implementado, não validado | Compilam; **nunca executados** | Rodar no Windows Sandbox (`tools/vm`) |
| Handler EcoQoS (ProcessPowerThrottling) | pendente | | |
| NVIDIA DRS | pendente | | |
| Tarefas agendadas (descoberta e handler) | pendente | | |
| CS2 `.cfg` | implementado, não validado | Testes com arquivos temporários | Detecção da pasta do jogo pendente |
| VALORANT | pendente | | |
| Captura PresentMon, comparação de sessões | pendente (parser e estatística: implementado, não validado) | Parser e estatística testados | |
| Investigador de stutters, laboratório A/B, relatórios, IA | pendente | | |
| Catálogo assinado (Ed25519) | pendente | | |

## Garantias estruturais testadas

- **A descoberta não escreve:** `ReadOnlyGuaranteeTests` lê os metadados do `Nexus.HardwareDiscovery.dll` e falha se ele
  referenciar APIs de escrita: `RegSetValue*`, `Power{Set,Write}*`, `ChangeServiceConfig`, `RegistryKey.SetValue`,
  `OpenSubKey` com escrita, `File.Write*`, `Process.Start` etc. Um teste de controle confirma que a inspeção pega
  essas chamadas no `Nexus.WindowsActions`.
- **A interface não executa:** `DesktopReadOnlyTests` lê o `Nexus.Desktop.dll` e falha se ele referenciar o construtor do
  `ActionExecutor`, `Execute`, `Revert`, `RecoveryService`, `TryAcquire`, o construtor de escrita do `SqliteJournal`
  ou `IActionHandler.Apply/Restore`. Ele também exige `SimulationOnlyHandler.Wrap` e `SqliteJournal.OpenReadOnly`.
- **Executor de produção exige a trava:** o construtor sem trava é `internal` (visível só aos testes).
